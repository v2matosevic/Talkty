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
    /// Configure the runtime library order based on GPU preference.
    /// Must be called before any WhisperFactory is created.
    /// </summary>
    private static void ConfigureRuntime(bool useGpu)
    {
        if (useGpu)
        {
            var cudaAvailable = CheckCudaAvailability();

            if (cudaAvailable)
            {
                Log.Info("Configuring runtime for CUDA GPU ONLY (no CPU fallback)...");
                RuntimeOptions.RuntimeLibraryOrder = [
                    RuntimeLibrary.Cuda
                    // NO CPU fallback - user explicitly wants GPU only
                ];
                Log.Info("RuntimeLibraryOrder set to: [CUDA] - CPU fallback DISABLED");
            }
            else
            {
                Log.Error("GPU requested but CUDA DLLs not available!");
                Log.Warning("Falling back to CPU since CUDA files are missing");
                RuntimeOptions.RuntimeLibraryOrder = [
                    RuntimeLibrary.Cpu
                ];
                Log.Info("RuntimeLibraryOrder set to: [CPU] (CUDA files missing)");
            }
        }
        else
        {
            Log.Info("Configuring runtime for CPU only...");
            RuntimeOptions.RuntimeLibraryOrder = [
                RuntimeLibrary.Cpu
            ];
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
                        Log.Warning("CUDA may not be installed. Install CUDA Toolkit 12.1+ for GPU acceleration.");
                        BackendInfo = "CPU (GPU unavailable - install CUDA Toolkit)";
                    }

                    // Log loaded whisper-related modules
                    Log.LogLoadedModules("whisper", "ggml", "cuda", "cublas");

                    // For multilingual models, use auto-detect to transcribe in original language
                    _currentLanguage = profile.SupportsAutoDetect() ? "auto" : "en";

                    Log.Debug($"Building WhisperProcessor with language={_currentLanguage}...");
                    var builder = _factory.CreateBuilder()
                        .WithThreads(Environment.ProcessorCount)
                        .WithLanguage(_currentLanguage);

                    // Do NOT use WithTranslate() - transcribe in original language
                    if (profile.SupportsAutoDetect())
                    {
                        Log.Debug("Whisper configured for auto language detection (transcribe in original language)");
                    }
                    else
                    {
                        Log.Debug("Whisper configured for English-only model");
                    }

                    _processor = builder.Build();

                    Log.Info($"WhisperProcessor built. Threads: {Environment.ProcessorCount}");
                    CurrentProfile = profile;
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

    public async Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples,
        TranscriptionOptions options,
        CancellationToken cancellationToken = default)
    {
        var audioDuration = audioSamples.Length / 16000.0; // 16kHz sample rate
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

            // Check if we need to rebuild processor for different language
            if (targetLanguage != _currentLanguage && CurrentProfile != null)
            {
                Log.Debug($"Language changed from {_currentLanguage} to {targetLanguage}, rebuilding processor...");
                await RebuildProcessorForLanguage(targetLanguage);
            }

            Log.Debug($"Starting Whisper processing with {options.TimeoutMs}ms timeout...");
            var result = await Task.Run(async () =>
            {
                var segments = new List<string>();
                int segmentCount = 0;

                await foreach (var segment in _processor.ProcessAsync(audioSamples, linkedCts.Token))
                {
                    linkedCts.Token.ThrowIfCancellationRequested();
                    segmentCount++;
                    Log.Debug($"Segment {segmentCount}: [{segment.Start:mm\\:ss} - {segment.End:mm\\:ss}] \"{segment.Text}\"");
                    segments.Add(segment.Text);
                }

                Log.Info($"Processing complete. Total segments: {segmentCount}");
                return string.Join(" ", segments).Trim();
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

    private Task RebuildProcessorForLanguage(string language)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_factory == null || CurrentProfile == null) return;

                // Dispose old processor
                _processor?.Dispose();

                var builder = _factory.CreateBuilder()
                    .WithThreads(Environment.ProcessorCount)
                    .WithLanguage(language);

                // Do NOT use WithTranslate() - transcribe in original language
                _processor = builder.Build();
                _currentLanguage = language;

                Log.Info($"Processor rebuilt for language: {language}");
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
