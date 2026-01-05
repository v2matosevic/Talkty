using Talkty.App.Models;

namespace Talkty.App.Services;

/// <summary>
/// Unified transcription service that manages multiple engine backends.
/// Automatically selects the appropriate engine based on the model profile.
/// </summary>
public class TranscriptionService : ITranscriptionService
{
    private ITranscriptionEngine? _currentEngine;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);
    private bool _isLoading;

    public bool IsModelLoaded => !_isLoading && (_currentEngine?.IsModelLoaded ?? false);
    public ModelProfile? CurrentProfile => _currentEngine?.CurrentProfile;
    public string? BackendInfo => _currentEngine?.BackendInfo;
    public IReadOnlyList<string> SupportedLanguages => _currentEngine?.SupportedLanguages ?? ["en"];

    public async Task<bool> LoadModelAsync(ModelProfile profile, string modelPath, bool useGpu = false)
    {
        Log.Info($"TranscriptionService.LoadModelAsync: Profile={profile}, Path={modelPath}, UseGpu={useGpu}");

        // Serialize load requests to prevent race conditions
        await _loadSemaphore.WaitAsync();
        _isLoading = true;

        try
        {
            return await Task.Run(async () =>
            {
                lock (_lock)
                {
                    // Check if we need to switch engines
                    var requiredEngine = profile.GetEngine();
                    var currentEngineType = _currentEngine?.EngineType;

                    if (currentEngineType != requiredEngine || _currentEngine == null)
                    {
                        Log.Info($"Switching engine from {currentEngineType} to {requiredEngine}");

                        // Dispose old engine
                        _currentEngine?.Dispose();

                        // Create new engine
                        _currentEngine = TranscriptionEngineFactory.CreateEngine(requiredEngine);
                        Log.Info($"Created new {_currentEngine.EngineName} engine");
                    }
                }

                // Load model (outside lock to allow async operation)
                return await _currentEngine!.LoadModelAsync(profile, modelPath, useGpu);
            });
        }
        finally
        {
            _isLoading = false;
            _loadSemaphore.Release();
        }
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples,
        string language = "en",
        CancellationToken cancellationToken = default)
    {
        Log.Info($"TranscriptionService.TranscribeAsync: Samples={audioSamples.Length}, Language={language}");

        if (_currentEngine == null)
        {
            Log.Error("TranscribeAsync called but no engine is loaded!");
            return new TranscriptionResult
            {
                Success = false,
                ErrorMessage = "No transcription engine loaded"
            };
        }

        if (!_currentEngine.IsModelLoaded)
        {
            Log.Error("TranscribeAsync called but model is not loaded!");
            return new TranscriptionResult
            {
                Success = false,
                ErrorMessage = "Model not loaded"
            };
        }

        // Validate language for current model
        var effectiveLanguage = language;
        if (CurrentProfile.HasValue)
        {
            var profile = CurrentProfile.Value;

            // If auto is requested but not supported, fall back to "en"
            if (language == "auto" && !profile.SupportsAutoDetect())
            {
                Log.Warning($"Auto-detect not supported by {profile}, falling back to 'en'");
                effectiveLanguage = "en";
            }
        }

        var options = new TranscriptionOptions
        {
            Language = effectiveLanguage,
            TimeoutMs = (int)ITranscriptionService.DefaultTimeout.TotalMilliseconds
        };

        return await _currentEngine.TranscribeAsync(audioSamples, options, cancellationToken);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_currentEngine != null)
            {
                Log.Debug($"Disposing {_currentEngine.EngineName} engine");
                _currentEngine.Dispose();
                _currentEngine = null;
            }
        }
        _loadSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
