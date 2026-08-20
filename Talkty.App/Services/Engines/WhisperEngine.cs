using System.IO;
using Talkty.App.Models;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Talkty.App.Services.Engines;

/// <summary>
/// Whisper.net engine implementation for Whisper-based models.
/// Supports: Tiny, Base, Small, Medium, Large, LargeTurbo, DistilLargeV3
/// </summary>
public class WhisperEngine : ITranscriptionEngine
{
    private WhisperProcessor? _processor;
    private WhisperFactory? _factory;
    private readonly object _lock = new();
    private string _currentLanguage = "en";
    private string? _currentVocabularyPrompt;
    private string? _languageHint;
    private bool _useGpu = false;

    // Fire-and-forget warmup on the freshly built processor. Tracked so any path that
    // disposes/replaces the processor can cancel it and wait it out first — disposing a
    // processor mid-ProcessAsync throws "Cannot dispose while processing" and (when it
    // happened inside a rebuild) killed the very transcription that triggered it.
    private Task? _warmupTask;
    private CancellationTokenSource? _warmupCts;

    public string EngineName => "Whisper";
    public TranscriptionEngine EngineType => TranscriptionEngine.Whisper;
    public bool IsModelLoaded => _processor != null;
    public ModelProfile? CurrentProfile { get; private set; }
    public string? BackendInfo { get; private set; }

    /// <summary>
    /// Check if CUDA runtime DLLs are available. The file list is
    /// <see cref="CudaPackService.RequiredFiles"/> — the single manifest shared with the
    /// in-app pack installer, so "installed" and "loadable" can never disagree.
    /// </summary>
    private static bool CheckCudaAvailability()
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var foundDlls = new List<string>();
            var missingDlls = new List<string>();

            foreach (var relative in CudaPackService.RequiredFiles)
            {
                var dllPath = Path.Combine(baseDir, relative);
                if (File.Exists(dllPath))
                {
                    var size = new FileInfo(dllPath).Length / (1024.0 * 1024.0);
                    foundDlls.Add($"{Path.GetFileName(relative)} ({size:F1} MB)");
                }
                else
                {
                    missingDlls.Add(relative);
                }
            }

            Log.Info($"CUDA DLLs found: {string.Join(", ", foundDlls)}");

            if (missingDlls.Count > 0)
            {
                Log.Warning($"CUDA DLLs missing: {string.Join(", ", missingDlls)}");
                return false;
            }

            Log.Info("All required CUDA DLLs present");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to check CUDA availability: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Check if Vulkan runtime DLLs are available (for AMD/Intel GPU acceleration).
    /// </summary>
    private static bool CheckVulkanAvailability()
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var vulkanRuntimePath = Path.Combine(baseDir, "runtimes", "vulkan", "win-x64");
            var vulkanDll = Path.Combine(vulkanRuntimePath, "ggml-vulkan-whisper.dll");

            Log.Debug($"Checking Vulkan availability: {vulkanDll}");

            if (File.Exists(vulkanDll))
            {
                var size = new FileInfo(vulkanDll).Length / (1024.0 * 1024.0);
                Log.Info($"Vulkan DLL found: ggml-vulkan-whisper.dll ({size:F1} MB)");
                return true;
            }

            Log.Debug("Vulkan DLL not found");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to check Vulkan availability: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Configure the runtime library order based on GPU preference.
    /// Priority: CUDA (NVIDIA) > Vulkan (AMD/Intel) > CPU.
    /// Must be called before any WhisperFactory is created.
    /// </summary>
    private static void ConfigureRuntime(bool useGpu)
    {
        if (useGpu)
        {
            var cudaAvailable = CheckCudaAvailability();

            if (cudaAvailable)
            {
                Log.Info("Configuring runtime: CUDA GPU (no fallback)");
                RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda];
                Log.Info("RuntimeLibraryOrder set to: [CUDA]");
                return;
            }

            var vulkanAvailable = CheckVulkanAvailability();
            if (vulkanAvailable)
            {
                Log.Info("Configuring runtime: Vulkan GPU (works on NVIDIA/AMD/Intel; CUDA pack not installed)");
                RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan];
                Log.Info("RuntimeLibraryOrder set to: [Vulkan]");
                return;
            }

            Log.Error("GPU requested but neither CUDA nor Vulkan are available — falling back to CPU");
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
            Log.Info("RuntimeLibraryOrder set to: [CPU] (no GPU runtime found)");
        }
        else
        {
            Log.Info("Configuring runtime: CPU");
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
            Log.Info("RuntimeLibraryOrder set to: [CPU]");
        }
    }

    /// <summary>
    /// Detect which runtime library was actually loaded by checking for loaded DLLs.
    /// </summary>
    private static string DetectLoadedRuntime()
    {
        try
        {
            // Check loaded modules to determine which backend is active
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var modules = process.Modules;

            foreach (System.Diagnostics.ProcessModule module in modules)
            {
                var name = module.ModuleName.ToLowerInvariant();

                if (name.Contains("cuda") || name.Contains("cublas") || name.Contains("cudnn"))
                    return "CUDA GPU (whisper.cpp)";

                if (name.Contains("vulkan"))
                    return "Vulkan GPU (whisper.cpp)";

                if (name.Contains("coreml"))
                    return "CoreML (whisper.cpp)";

                if (name.Contains("openvino"))
                    return "OpenVINO (whisper.cpp)";
            }

            // Default to CPU if no GPU libraries detected
            return "CPU (whisper.cpp)";
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to detect runtime: {ex.Message}");
            return "Unknown (whisper.cpp)";
        }
    }

    public IReadOnlyList<string> SupportedLanguages =>
        CurrentProfile?.GetSupportedLanguages() ?? ["en"];

    public bool CanHandleProfile(ModelProfile profile) =>
        profile.GetEngine() == TranscriptionEngine.Whisper;

    /// <summary>
    /// Pre-sets the vocabulary prompt so the processor is built with it on model load.
    /// Call before LoadModelAsync to avoid a rebuild on first transcription.
    /// </summary>
    public void SetVocabularyPrompt(string? prompt)
    {
        _currentVocabularyPrompt = prompt;
    }

    /// <summary>
    /// Pre-sets the language the processor should be built with on model load.
    /// Without this, every load (including idle-unload reloads) built the processor with
    /// the model's default ("auto" for multilingual) and the first transcription with the
    /// user's real language forced a full processor rebuild.
    /// </summary>
    public void SetLanguageHint(string? language)
    {
        _languageHint = language;
    }

    public async Task<bool> LoadModelAsync(
        ModelProfile profile,
        string modelPath,
        bool useGpu = false,
        CancellationToken cancellationToken = default)
    {
        if (!CanHandleProfile(profile))
        {
            Log.Error($"WhisperEngine cannot handle profile: {profile}");
            return false;
        }

        Log.Info($"WhisperEngine.LoadModelAsync: Profile={profile}, Path={modelPath}, UseGpu={useGpu}");
        _useGpu = useGpu;

        return await Task.Run(() =>
        {
            lock (_lock)
            {
                try
                {
                    // Dispose previous model
                    DisposeInternal();

                    if (!File.Exists(modelPath))
                    {
                        Log.Error($"Model file does not exist: {modelPath}");
                        BackendInfo = "Model file not found";
                        return false;
                    }

                    var fileSize = new FileInfo(modelPath).Length / (1024.0 * 1024.0);
                    Log.Info($"Model file found. Size: {fileSize:F1} MB");

                    Log.Section("WHISPER MODEL LOADING");
                    Log.Info($"Model: {profile}");
                    Log.Info($"Path: {modelPath}");
                    Log.Info($"File size: {fileSize:F1} MB");
                    Log.Info($"GPU requested: {useGpu}");

                    // Configure runtime library order before creating factory
                    ConfigureRuntime(useGpu);

                    Log.Debug("Creating WhisperFactory...");
                    var factoryStart = DateTime.Now;

                    // No CPU fallback - if GPU is requested and CUDA fails, let it fail
                    _factory = WhisperFactory.FromPath(modelPath);

                    var factoryTime = DateTime.Now - factoryStart;
                    Log.Info($"WhisperFactory created in {factoryTime.TotalMilliseconds:F0}ms");

                    // Detect which runtime was actually loaded
                    var loadedRuntime = DetectLoadedRuntime();
                    BackendInfo = loadedRuntime;
                    Log.Info($"Backend detected: {loadedRuntime}");

                    // Warn if GPU was requested but CPU is being used
                    if (useGpu && loadedRuntime.Contains("CPU"))
                    {
                        Log.Warning("GPU was requested but CPU is being used!");
                        Log.Warning("No GPU runtime loaded. Vulkan is bundled and covers NVIDIA/AMD/Intel — check GPU drivers. NVIDIA users can also install the CUDA pack from Settings > Behavior.");
                        BackendInfo = "CPU (GPU unavailable — check GPU drivers/CUDA)";
                    }

                    // Log loaded whisper-related modules
                    Log.LogLoadedModules("whisper", "ggml", "cuda", "cublas");

                    // Build with the user's configured language when we have it — English-only
                    // models are always "en". Falling back to "auto" here used to force a
                    // processor rebuild on the first transcription after every (re)load.
                    _currentLanguage = profile.SupportsAutoDetect() ? (_languageHint ?? "auto") : "en";

                    var threads = GetOptimalThreadCount();
                    Log.Debug($"Building WhisperProcessor with language={_currentLanguage}, threads={threads}, vocabulary={(!string.IsNullOrWhiteSpace(_currentVocabularyPrompt) ? $"{_currentVocabularyPrompt.Length} chars" : "none")}...");
                    _processor = BuildProcessor(_factory, _currentLanguage, threads, profile.SupportsAutoDetect(), _currentVocabularyPrompt);

                    Log.Info($"WhisperProcessor built. Threads: {threads}");
                    CurrentProfile = profile;

                    // Warmup runs OFF the load path — fire-and-forget. UI sees "Ready" immediately;
                    // first real transcription pays a small cold-start cost if it beats warmup, but
                    // that's rare and better than blocking model load. Tracked (task + cts) so
                    // rebuild/dispose can wait it out instead of disposing the processor under it.
                    _warmupCts?.Dispose();
                    _warmupCts = new CancellationTokenSource();
                    var warmupProcessor = _processor;
                    var warmupToken = _warmupCts.Token;
                    _warmupTask = Task.Run(() => WarmupProcessor(warmupProcessor, warmupToken));

                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error("WhisperEngine.LoadModelAsync failed", ex);
                    BackendInfo = $"Error: {ex.Message}";
                    return false;
                }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Prime the processor with a tiny silent audio sample.
    /// This warms up JIT compilation, GPU memory allocation, and internal whisper.cpp buffers
    /// so the first real transcription doesn't pay a cold-start penalty.
    /// </summary>
    private static async Task WarmupProcessor(WhisperProcessor processor, CancellationToken cancellationToken)
    {
        try
        {
            var warmupStart = DateTime.Now;
            // 0.5 seconds of silence at 16kHz — primes JIT, GPU memory, internal buffers
            var silence = new float[Constants.WhisperWarmupSamples];
            using var timeoutCts = new CancellationTokenSource(5000);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            await foreach (var segment in processor.ProcessAsync(silence, linkedCts.Token))
            {
                // Discard — we only care about priming the pipeline
            }
            var warmupTime = DateTime.Now - warmupStart;
            Log.Info($"Processor warmup completed in {warmupTime.TotalMilliseconds:F0}ms");
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Processor warmup cancelled");
        }
        catch (Exception ex)
        {
            // Non-fatal — warmup is best-effort
            Log.Warning($"Processor warmup failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>
    /// Cancels any in-flight warmup and waits (bounded) for it to finish so the processor it
    /// runs on can be disposed or reused safely. Returns true when warmup is confirmed done.
    ///
    /// The task/cts pair is snapshotted under <see cref="_lock"/> so a concurrent
    /// <see cref="LoadModelAsync"/> (which disposes and replaces the cts under the same lock)
    /// can never hand us a mismatched pair; Cancel on an already-disposed cts is swallowed.
    /// Warmup itself never takes the lock, so calling this with or without the lock held
    /// cannot deadlock (Monitor is reentrant).
    /// </summary>
    private bool CancelWarmupAndWait()
    {
        Task? task;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            task = _warmupTask;
            cts = _warmupCts;
        }
        if (task == null || task.IsCompleted) return true;

        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* replaced by a concurrent load — task is winding down */ }

        bool finished;
        try
        {
            finished = task.Wait(TimeSpan.FromSeconds(6));
        }
        catch (AggregateException)
        {
            // Warmup swallows its own exceptions; a fault still means it has finished.
            finished = true;
        }

        if (!finished)
        {
            Log.Warning("Warmup did not finish within 6s — its processor must not be disposed");
        }

        lock (_lock)
        {
            if (_warmupTask == task) _warmupTask = null;
        }
        return finished;
    }

    /// <summary>
    /// Async twin of <see cref="CancelWarmupAndWait"/> for the transcription path: a real
    /// transcription supersedes the warmup, and <see cref="WhisperProcessor"/> holds a single
    /// native state — two concurrent ProcessAsync calls on one instance corrupt it. Called
    /// before every ProcessAsync so a short dictation right after an idle-unload reload can't
    /// overlap the still-running warmup (the language-hint fix removed the processor rebuild
    /// that used to serialize them by accident).
    /// </summary>
    private async Task<bool> CancelWarmupAndWaitAsync()
    {
        Task? task;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            task = _warmupTask;
            cts = _warmupCts;
        }
        if (task == null || task.IsCompleted) return true;

        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(6));
        }
        catch (TimeoutException)
        {
            // Native decode is wedged — running a second ProcessAsync on the same state
            // would corrupt it. The caller fails the transcription instead.
            Log.Error("Warmup still running after cancel + 6s wait — refusing concurrent processing");
            return false;
        }

        lock (_lock)
        {
            if (_warmupTask == task) _warmupTask = null;
        }
        return true;
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples,
        TranscriptionOptions options,
        CancellationToken cancellationToken = default)
    {
        var audioDuration = audioSamples.Length / (double)Constants.SampleRate;
        Log.Section("TRANSCRIPTION");
        Log.Info($"Audio: {audioSamples.Length} samples ({audioDuration:F1}s)");
        Log.Info($"Language: {options.Language}");
        Log.Info($"Timeout: {options.TimeoutMs}ms");
        Log.Info($"Backend: {BackendInfo}");

        if (_processor == null || _factory == null)
        {
            Log.Error("TranscribeAsync called but processor is null!");
            return new TranscriptionResult
            {
                Success = false,
                ErrorMessage = "Model not loaded"
            };
        }

        var startTime = DateTime.Now;

        // Create timeout
        using var timeoutCts = new CancellationTokenSource(options.TimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            // Use the language from options (user's choice from settings)
            // If user selects a specific language like "de" or "hr", use that
            // If user selects "auto", Whisper will auto-detect (may be unreliable for some languages)
            var targetLanguage = options.Language;

            // For multilingual models, validate the language is supported
            if (CurrentProfile?.SupportsAutoDetect() == true)
            {
                Log.Debug($"Multilingual model: using language '{targetLanguage}' for transcription");
            }

            // Check if we need to rebuild processor for different language or vocabulary
            var vocabularyPrompt = options.VocabularyPrompt;
            var languageChanged = targetLanguage != _currentLanguage;
            var vocabularyChanged = vocabularyPrompt != _currentVocabularyPrompt;

            if ((languageChanged || vocabularyChanged) && CurrentProfile != null)
            {
                Log.Debug($"Processor rebuild needed: languageChanged={languageChanged}, vocabularyChanged={vocabularyChanged}");
                await RebuildProcessor(targetLanguage, vocabularyPrompt);
            }

            // A real transcription supersedes the warmup — wind it down first. The processor
            // holds a single native state; two concurrent ProcessAsync calls corrupt it.
            if (!await CancelWarmupAndWaitAsync())
            {
                return new TranscriptionResult
                {
                    Success = false,
                    ErrorMessage = "Engine is busy — please try again",
                    Timestamp = startTime,
                    Duration = DateTime.Now - startTime
                };
            }

            Log.Debug($"Starting Whisper processing with {options.TimeoutMs}ms timeout...");
            var result = await Task.Run(async () =>
            {
                var segments = new List<string>();
                int segmentCount = 0;
                bool firstSegmentFired = false;

                await foreach (var segment in _processor.ProcessAsync(audioSamples, linkedCts.Token))
                {
                    linkedCts.Token.ThrowIfCancellationRequested();
                    segmentCount++;
                    Log.Debug($"Segment {segmentCount}: [{segment.Start:mm\\:ss} - {segment.End:mm\\:ss}] \"{segment.Text}\"");
                    segments.Add(segment.Text);

                    // Fire first-segment callback so caller can copy to clipboard immediately
                    if (!firstSegmentFired && options.OnFirstSegment != null && !string.IsNullOrWhiteSpace(segment.Text))
                    {
                        try
                        {
                            options.OnFirstSegment(segment.Text.Trim());
                            firstSegmentFired = true;
                            Log.Debug("First segment callback fired");
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"First segment callback failed: {ex.Message}");
                        }
                    }
                }

                Log.Info($"Processing complete. Total segments: {segmentCount}");
                // Smart join: merges fragments split by pauses, preserves real sentence breaks
                return segmentCount > 1
                    ? TextPostProcessor.JoinSegments(segments)
                    : string.Join(" ", segments).Trim();
            }, linkedCts.Token);

            var elapsed = DateTime.Now - startTime;
            var realTimeFactor = elapsed.TotalSeconds / audioDuration;
            Log.Info($"Transcription completed:");
            Log.Info($"  Time: {elapsed.TotalMilliseconds:F0}ms");
            Log.Info($"  Real-time factor: {realTimeFactor:F2}x (lower is faster)");
            Log.Info($"  Result: {result.Length} chars");
            if (realTimeFactor > 1.0)
                Log.Warning($"  Slower than real-time! Consider a smaller model or GPU.");

            return new TranscriptionResult
            {
                Text = result,
                Timestamp = startTime,
                Duration = elapsed,
                Success = true
            };
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            var elapsed = DateTime.Now - startTime;
            Log.Error($"Transcription timed out after {elapsed.TotalSeconds:F1}s");
            return new TranscriptionResult
            {
                Success = false,
                ErrorMessage = $"Transcription timed out after {options.TimeoutMs / 1000} seconds",
                Timestamp = startTime,
                Duration = elapsed
            };
        }
        catch (OperationCanceledException)
        {
            var elapsed = DateTime.Now - startTime;
            Log.Info($"Transcription cancelled after {elapsed.TotalSeconds:F1}s");
            return new TranscriptionResult
            {
                Success = false,
                ErrorMessage = "Transcription was cancelled",
                Timestamp = startTime,
                Duration = elapsed
            };
        }
        catch (Exception ex)
        {
            Log.Error("WhisperEngine.TranscribeAsync exception", ex);
            return new TranscriptionResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Timestamp = startTime,
                Duration = DateTime.Now - startTime
            };
        }
    }

    /// <summary>
    /// Optimal thread count: use estimated physical cores (not logical/HT) capped at 8.
    /// Whisper.cpp is compute-bound — hyperthreading causes cache thrashing, not speedup.
    /// </summary>
    private static int GetOptimalThreadCount()
    {
        var logicalCores = Environment.ProcessorCount;
        var estimatedPhysicalCores = Math.Max(1, logicalCores / 2);
        var threads = Math.Min(Constants.WhisperMaxThreads, estimatedPhysicalCores);
        Log.Debug($"Thread selection: {logicalCores} logical cores → {estimatedPhysicalCores} estimated physical → using {threads}");
        return threads;
    }

    /// <summary>
    /// Builds a WhisperProcessor with speed-optimized settings.
    /// Greedy decoding (bestOf=1), no context carryover, temperature 0 with the standard
    /// fallback increment so a stuck decode (repetition loop / garbage segment) can re-decode.
    /// </summary>
    private static WhisperProcessor BuildProcessor(WhisperFactory factory, string language, int threads, bool isMultilingual, string? vocabularyPrompt = null)
    {
        // Use greedy decoding (fastest) — sub-builder sets BestOf=1 (single candidate)
        var greedyBuilder = factory.CreateBuilder()
            .WithGreedySamplingStrategy();
        if (greedyBuilder is Whisper.net.GreedySamplingStrategyBuilder greedy)
        {
            greedy.WithBestOf(1);
        }

        // Get back to the main builder via ParentBuilder
        var builder = greedyBuilder.ParentBuilder
            .WithThreads(threads)
            .WithLanguage(language)
            // Each recording is independent — don't carry context from previous transcriptions
            .WithNoContext()
            // Deterministic first pass (temperature 0). TemperatureInc 0.2 keeps whisper.cpp's
            // built-in fallback: when a window decodes badly (high compression ratio / low
            // logprob — a repetition loop or garbage), it re-decodes at higher temperature.
            // Costs nothing on healthy audio; disabling it (0f) left stuck decodes stuck.
            .WithTemperature(0f)
            .WithTemperatureInc(0.2f);

        if (!string.IsNullOrWhiteSpace(vocabularyPrompt))
        {
            builder.WithPrompt(vocabularyPrompt);
            Log.Debug($"Vocabulary prompt applied ({vocabularyPrompt.Length} chars)");
        }

        if (isMultilingual)
        {
            Log.Debug("Whisper configured for auto language detection (transcribe in original language)");
        }
        else
        {
            Log.Debug("Whisper configured for English-only model");
        }

        return builder.Build();
    }

    private Task RebuildProcessor(string language, string? vocabularyPrompt)
    {
        return Task.Run(() =>
        {
            // The warmup may still be processing on the current processor — let it wind
            // down before disposing, or Dispose throws "Cannot dispose while processing".
            var warmupDone = CancelWarmupAndWait();

            lock (_lock)
            {
                if (_factory == null || CurrentProfile == null) return;

                if (warmupDone)
                {
                    _processor?.Dispose();
                }
                else
                {
                    // Deliberate leak: disposing a processor whose native decode is wedged
                    // throws/corrupts. One abandoned processor beats a lost dictation.
                    Log.Warning("Abandoning busy processor instead of disposing it");
                }

                var threads = GetOptimalThreadCount();
                _processor = BuildProcessor(_factory, language, threads, CurrentProfile.Value.SupportsAutoDetect(), vocabularyPrompt);
                _currentLanguage = language;
                _currentVocabularyPrompt = vocabularyPrompt;

                Log.Info($"Processor rebuilt for language: {language}, vocabulary: {(vocabularyPrompt != null ? $"{vocabularyPrompt.Length} chars" : "none")}");
            }
        });
    }

    private void DisposeInternal()
    {
        // Wind down any in-flight warmup before touching the processor it runs on.
        if (!CancelWarmupAndWait())
        {
            // Native decode is wedged mid-flight: disposing the processor throws and
            // disposing the factory under a live processor is native-unsafe. Abandon both —
            // one leaked model beats a crash; process teardown reclaims the memory.
            Log.Error("Warmup still running — abandoning processor and factory instead of disposing");
            _processor = null;
            _factory = null;
            CurrentProfile = null;
            return;
        }

        if (_processor != null)
        {
            Log.Debug("Disposing WhisperProcessor");
            _processor.Dispose();
            _processor = null;
        }

        if (_factory != null)
        {
            Log.Debug("Disposing WhisperFactory");
            _factory.Dispose();
            _factory = null;
        }

        CurrentProfile = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            DisposeInternal();
            _warmupCts?.Dispose();
            _warmupCts = null;
        }
        GC.SuppressFinalize(this);
    }
}
