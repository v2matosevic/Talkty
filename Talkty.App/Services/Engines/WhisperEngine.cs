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
    private bool _useGpu = false;

    public string EngineName => "Whisper";
    public TranscriptionEngine EngineType => TranscriptionEngine.Whisper;
    public bool IsModelLoaded => _processor != null;
    public ModelProfile? CurrentProfile { get; private set; }
    public string? BackendInfo { get; private set; }

    /// <summary>
    /// Check if CUDA runtime DLLs are available.
    /// CUDA DLLs must be in the main app directory for Windows DLL loader to find them.
    /// </summary>
    private static bool CheckCudaAvailability()
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var cudaRuntimePath = Path.Combine(baseDir, "runtimes", "cuda", "win-x64");

            Log.Debug($"Checking CUDA availability...");
            Log.Debug($"  App directory: {baseDir}");
            Log.Debug($"  CUDA runtime path: {cudaRuntimePath}");

            // ggml-cuda-whisper.dll should be in runtimes/cuda/win-x64 (from NuGet)
            var whisperCudaDll = Path.Combine(cudaRuntimePath, "ggml-cuda-whisper.dll");

            // CUDA runtime DLLs (cublas, cudart) must be in main app directory
            // for Windows DLL loader to find them when ggml-cuda-whisper.dll loads
            var cudaDlls = new[]
            {
                ("cublas64_13.dll", baseDir),
                ("cublasLt64_13.dll", baseDir),
                ("cudart64_13.dll", baseDir),
                ("ggml-cuda-whisper.dll", cudaRuntimePath)
            };

            var foundDlls = new List<string>();
            var missingDlls = new List<string>();

            foreach (var (dll, searchPath) in cudaDlls)
            {
                var dllPath = Path.Combine(searchPath, dll);
                if (File.Exists(dllPath))
                {
                    var size = new FileInfo(dllPath).Length / (1024.0 * 1024.0);
                    foundDlls.Add($"{dll} ({size:F1} MB)");
                }
                else
                {
                    missingDlls.Add($"{dll} (expected in {searchPath})");
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
                Log.Info("Configuring runtime: Vulkan GPU (AMD/Intel — no CUDA found)");
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
                        Log.Warning("No GPU runtime found. For NVIDIA: install CUDA Toolkit 12.1+. For AMD/Intel: Vulkan runtime should be bundled.");
                        BackendInfo = "CPU (GPU unavailable — check GPU drivers/CUDA)";
                    }

                    // Log loaded whisper-related modules
                    Log.LogLoadedModules("whisper", "ggml", "cuda", "cublas");

                    // Use "auto" for multilingual models, "en" for English-only
                    _currentLanguage = profile.SupportsAutoDetect() ? "auto" : "en";

                    var threads = GetOptimalThreadCount();
                    Log.Debug($"Building WhisperProcessor with language={_currentLanguage}, threads={threads}, vocabulary={(!string.IsNullOrWhiteSpace(_currentVocabularyPrompt) ? $"{_currentVocabularyPrompt.Length} chars" : "none")}...");
                    _processor = BuildProcessor(_factory, _currentLanguage, threads, profile.SupportsAutoDetect(), _currentVocabularyPrompt);

                    Log.Info($"WhisperProcessor built. Threads: {threads}");
                    CurrentProfile = profile;

                    // Warmup runs OFF the load path — fire-and-forget. UI sees "Ready" immediately;
                    // first real transcription pays a small cold-start cost if it beats warmup, but
                    // that's rare and better than blocking model load.
                    _ = Task.Run(WarmupProcessor);

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
    private async Task WarmupProcessor()
    {
        // Snapshot the processor ref under the lock so we don't race with a reload/dispose.
        WhisperProcessor? processor;
        lock (_lock)
        {
            processor = _processor;
        }
        if (processor == null) return;

        try
        {
            var warmupStart = DateTime.Now;
            // 0.5 seconds of silence at 16kHz — primes JIT, GPU memory, internal buffers
            var silence = new float[Constants.WhisperWarmupSamples];
            using var cts = new CancellationTokenSource(5000);
            await foreach (var segment in processor.ProcessAsync(silence, cts.Token))
            {
                // Discard — we only care about priming the pipeline
            }
            var warmupTime = DateTime.Now - warmupStart;
            Log.Info($"Processor warmup completed in {warmupTime.TotalMilliseconds:F0}ms");
        }
        catch (Exception ex)
        {
            // Non-fatal — warmup is best-effort
            Log.Warning($"Processor warmup failed (non-fatal): {ex.Message}");
        }
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
            lock (_lock)
            {
                if (_factory == null || CurrentProfile == null) return;

                _processor?.Dispose();

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
        }
        GC.SuppressFinalize(this);
    }
}
