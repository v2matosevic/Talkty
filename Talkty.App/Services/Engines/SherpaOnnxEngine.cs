using System.IO;
using Talkty.App.Models;

namespace Talkty.App.Services.Engines;

/// <summary>
/// Sherpa-ONNX engine implementation for SenseVoice and other ONNX models.
/// Currently supports: SenseVoice
/// </summary>
public class SherpaOnnxEngine : ITranscriptionEngine
{
    private SherpaOnnx.OfflineRecognizer? _recognizer;
    private readonly object _lock = new();
    private string _modelDirectory = "";

    public string EngineName => "SherpaOnnx";
    public TranscriptionEngine EngineType => TranscriptionEngine.SherpaOnnx;
    public bool IsModelLoaded => _recognizer != null;
    public ModelProfile? CurrentProfile { get; private set; }
    public string? BackendInfo { get; private set; }

    public IReadOnlyList<string> SupportedLanguages =>
        CurrentProfile?.GetSupportedLanguages() ?? ["en"];

    public bool CanHandleProfile(ModelProfile profile) =>
        profile.GetEngine() == TranscriptionEngine.SherpaOnnx;

    public async Task<bool> LoadModelAsync(
        ModelProfile profile,
        string modelPath,
        bool useGpu = false,
        CancellationToken cancellationToken = default)
    {
        if (!CanHandleProfile(profile))
        {
            Log.Error($"SherpaOnnxEngine cannot handle profile: {profile}");
            return false;
        }

        Log.Info($"SherpaOnnxEngine.LoadModelAsync: Profile={profile}, Path={modelPath}");

        return await Task.Run(() =>
        {
            lock (_lock)
            {
                try
                {
                    // Dispose previous recognizer
                    DisposeInternal();

                    // For sherpa-onnx, the path should be a directory containing model files
                    if (!Directory.Exists(modelPath))
                    {
                        Log.Error($"Model directory does not exist: {modelPath}");
                        BackendInfo = "Model directory not found";
                        return false;
                    }

                    _modelDirectory = modelPath;

                    // Create config based on model profile
                    SherpaOnnx.OfflineRecognizerConfig? nullableConfig = CreateConfigForProfile(profile, modelPath);
                    if (nullableConfig == null)
                    {
                        Log.Error($"Failed to create config for profile: {profile}");
                        BackendInfo = "Invalid model configuration";
                        return false;
                    }
                    SherpaOnnx.OfflineRecognizerConfig config = (SherpaOnnx.OfflineRecognizerConfig)nullableConfig;

                    Log.Debug("Creating OfflineRecognizer...");
                    _recognizer = new SherpaOnnx.OfflineRecognizer(config);

                    CurrentProfile = profile;
                    BackendInfo = "CPU (sherpa-onnx)";
                    Log.Info($"SherpaOnnx recognizer created for {profile}");
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error("SherpaOnnxEngine.LoadModelAsync failed", ex);
                    BackendInfo = $"Error: {ex.Message}";
                    return false;
                }
            }
        }, cancellationToken);
    }

    private SherpaOnnx.OfflineRecognizerConfig? CreateConfigForProfile(ModelProfile profile, string modelDir)
    {
        var config = new SherpaOnnx.OfflineRecognizerConfig();
        config.ModelConfig = new SherpaOnnx.OfflineModelConfig();
        config.ModelConfig.Debug = 0;
        config.ModelConfig.NumThreads = Environment.ProcessorCount;

        // Always use CPU for sherpa-onnx (GPU requires specific builds)
        config.ModelConfig.Provider = "cpu";
        Log.Info("SherpaOnnx using CPU provider");

        switch (profile)
        {
            case ModelProfile.SenseVoice:
                return CreateSenseVoiceConfig(modelDir, config);

            default:
                Log.Warning($"Unknown SherpaOnnx profile: {profile}");
                return null;
        }
    }

    private SherpaOnnx.OfflineRecognizerConfig? CreateSenseVoiceConfig(
        string modelDir,
        SherpaOnnx.OfflineRecognizerConfig config)
    {
        // SenseVoice model structure:
        // - model.onnx (or model.int8.onnx)
        // - tokens.txt
        var modelFile = Path.Combine(modelDir, "model.onnx");
        var int8Model = Path.Combine(modelDir, "model.int8.onnx");
        var tokensFile = Path.Combine(modelDir, "tokens.txt");

        // Prefer int8 quantized model if available
        if (File.Exists(int8Model))
        {
            modelFile = int8Model;
        }

        if (!File.Exists(modelFile))
        {
            Log.Error($"SenseVoice model file not found: {modelFile}");
            return null;
        }

        if (!File.Exists(tokensFile))
        {
            Log.Error($"SenseVoice tokens file not found: {tokensFile}");
            return null;
        }

        config.ModelConfig.SenseVoice = new SherpaOnnx.OfflineSenseVoiceModelConfig();
        config.ModelConfig.SenseVoice.Model = modelFile;
        config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1; // 1 = true, 0 = false
        config.ModelConfig.Tokens = tokensFile;

        Log.Info($"SenseVoice config created. Model: {modelFile}");
        return config;
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples,
        TranscriptionOptions options,
        CancellationToken cancellationToken = default)
    {
        Log.Info($"SherpaOnnxEngine.TranscribeAsync: Samples={audioSamples.Length}, Language={options.Language}");

        if (_recognizer == null)
        {
            Log.Error("TranscribeAsync called but recognizer is null!");
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
            var result = await Task.Run(() =>
            {
                linkedCts.Token.ThrowIfCancellationRequested();

                // Create stream and process audio
                var stream = _recognizer.CreateStream();
                stream.AcceptWaveform(16000, audioSamples);

                linkedCts.Token.ThrowIfCancellationRequested();

                // Decode
                _recognizer.Decode(stream);

                linkedCts.Token.ThrowIfCancellationRequested();

                // Get result
                var text = stream.Result.Text;
                Log.Debug($"SherpaOnnx result: \"{text}\"");

                return text?.Trim() ?? "";
            }, linkedCts.Token);

            var elapsed = DateTime.Now - startTime;
            Log.Info($"Transcription finished in {elapsed.TotalMilliseconds:F0}ms. Result length: {result.Length}");

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
            Log.Error("SherpaOnnxEngine.TranscribeAsync exception", ex);
            return new TranscriptionResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Timestamp = startTime,
                Duration = DateTime.Now - startTime
            };
        }
    }

    private void DisposeInternal()
    {
        if (_recognizer != null)
        {
            Log.Debug("Disposing SherpaOnnx recognizer");
            _recognizer.Dispose();
            _recognizer = null;
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
