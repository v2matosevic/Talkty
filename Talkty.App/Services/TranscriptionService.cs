using Talkty.App.Models;

namespace Talkty.App.Services;

/// <summary>
/// Unified transcription service that manages multiple engine backends.
/// Automatically selects the appropriate engine based on the model profile.
///
/// IDLE UNLOAD: the app lives in the tray, but a loaded Whisper model holds hundreds of MB
/// to multiple GB of RAM (and VRAM on GPU) around the clock. After a period with no
/// transcriptions the engine is disposed to reclaim that memory, and transparently reloaded
/// on the next use. The reload is kicked off when recording STARTS (see MainViewModel), so
/// it overlaps with the user speaking and is usually free in wall-clock terms;
/// <see cref="EnsureModelLoadedAsync"/> at transcription time awaits any in-flight reload.
/// Cloud profiles are never unloaded — they hold no local memory.
/// </summary>
public class TranscriptionService : ITranscriptionService
{
    private ITranscriptionEngine? _currentEngine;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);
    private bool _isLoading;

    public bool IsModelLoaded => !_isLoading && (_currentEngine?.IsModelLoaded ?? false);
    public ModelProfile? CurrentProfile => _currentEngine?.CurrentProfile ?? (_unloadedForIdle ? _lastProfile : null);
    public string? BackendInfo => _currentEngine?.BackendInfo;
    public IReadOnlyList<string> SupportedLanguages => _currentEngine?.SupportedLanguages ?? ["en"];

    private string? _pendingVocabularyPrompt;
    private string? _pendingApiKey;

    // Last successful load — what EnsureModelLoadedAsync restores after an idle unload.
    private ModelProfile? _lastProfile;
    private string? _lastModelPath;
    private bool _lastUseGpu;
    private volatile bool _unloadedForIdle;

    // One-shot idle timer, re-armed after every load and transcription. _activeTranscriptions
    // guards the race where the timer fires just as a transcription begins: the transcription
    // increments the counter FIRST, then awaits the semaphore the unload holds — so the unload
    // either sees the counter and skips, or finishes and the transcription reloads.
    private Timer? _idleTimer;
    private int _activeTranscriptions;
    private volatile bool _idleUnloadEnabled = true;
    private bool _disposed;

    public void SetVocabularyPrompt(string? prompt)
    {
        _pendingVocabularyPrompt = prompt;
        // If engine already exists, forward to it
        if (_currentEngine is Engines.WhisperEngine whisper)
        {
            whisper.SetVocabularyPrompt(prompt);
        }
    }

    public void SetCloudApiKey(string? apiKey)
    {
        _pendingApiKey = apiKey;
        // If the cloud engine already exists, forward to it
        if (_currentEngine is Engines.OpenRouterEngine openRouter)
        {
            openRouter.SetApiKey(apiKey);
        }
    }

    public void SetIdleUnload(bool enabled)
    {
        if (_idleUnloadEnabled == enabled) return;
        _idleUnloadEnabled = enabled;
        Log.Info($"Idle model unload {(enabled ? "enabled" : "disabled")}");

        if (!enabled)
        {
            StopIdleTimer();
        }
        else if (IsModelLoaded)
        {
            ArmIdleTimer();
        }
    }

    public async Task<bool> LoadModelAsync(ModelProfile profile, string modelPath, bool useGpu = false)
    {
        Log.Info($"TranscriptionService.LoadModelAsync: Profile={profile}, Path={modelPath}, UseGpu={useGpu}");

        // Serialize load requests to prevent race conditions
        await _loadSemaphore.WaitAsync();
        try
        {
            return await LoadModelCoreAsync(profile, modelPath, useGpu);
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    /// <summary>
    /// Actual load logic. Caller MUST hold <see cref="_loadSemaphore"/> — shared by the
    /// public load path and the idle-reload path so they can't race each other.
    /// </summary>
    private async Task<bool> LoadModelCoreAsync(ModelProfile profile, string modelPath, bool useGpu)
    {
        _isLoading = true;
        try
        {
            var loaded = await Task.Run(async () =>
            {
                lock (_lock)
                {
                    // Check if we need to switch engines (an idle unload leaves _currentEngine
                    // null, which lands in the create path and re-forwards prompt + API key)
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

                        // Forward pending vocabulary prompt to new engine
                        if (_pendingVocabularyPrompt != null && _currentEngine is Engines.WhisperEngine newWhisper)
                        {
                            newWhisper.SetVocabularyPrompt(_pendingVocabularyPrompt);
                        }

                        // Forward pending API key to a new cloud engine
                        if (_currentEngine is Engines.OpenRouterEngine newOpenRouter)
                        {
                            newOpenRouter.SetApiKey(_pendingApiKey);
                        }
                    }
                }

                // Load model (outside lock to allow async operation)
                return await _currentEngine!.LoadModelAsync(profile, modelPath, useGpu);
            });

            if (loaded)
            {
                _lastProfile = profile;
                _lastModelPath = modelPath;
                _lastUseGpu = useGpu;
                _unloadedForIdle = false;
                ArmIdleTimer();
            }

            return loaded;
        }
        finally
        {
            _isLoading = false;
        }
    }

    public async Task<bool> EnsureModelLoadedAsync()
    {
        // Fast path: engine alive — nothing to do.
        if (_currentEngine?.IsModelLoaded == true)
            return true;

        // Nothing was ever loaded (startup failure / no model downloaded) — can't restore.
        if (_lastProfile is not { } profile || _lastModelPath == null)
            return false;

        await _loadSemaphore.WaitAsync();
        try
        {
            // Re-check under the semaphore — a concurrent Ensure may have already reloaded.
            if (_currentEngine?.IsModelLoaded == true)
                return true;

            Log.Info($"Reloading model after idle unload: {profile}");
            return await LoadModelCoreAsync(profile, _lastModelPath, _lastUseGpu);
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples,
        string language = "en",
        CancellationToken cancellationToken = default,
        Action<string>? onFirstSegment = null,
        string? vocabularyPrompt = null)
    {
        Log.Info($"TranscriptionService.TranscribeAsync: Samples={audioSamples.Length}, Language={language}{(vocabularyPrompt != null ? $", Vocabulary={vocabularyPrompt.Length} chars" : "")}");

        // Counter BEFORE EnsureModelLoadedAsync — see the race note on _idleTimer.
        Interlocked.Increment(ref _activeTranscriptions);
        StopIdleTimer();
        try
        {
            // Transparently restore the model if it was unloaded while idle. Usually a no-op:
            // the reload was already kicked off at recording start and finished during speech.
            await EnsureModelLoadedAsync();

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
                TimeoutMs = (int)ITranscriptionService.DefaultTimeout.TotalMilliseconds,
                OnFirstSegment = onFirstSegment,
                VocabularyPrompt = vocabularyPrompt
            };

            return await _currentEngine.TranscribeAsync(audioSamples, options, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _activeTranscriptions);
            ArmIdleTimer();
        }
    }

    // ─── Idle unload machinery ──────────────────────────────────────────

    private void ArmIdleTimer()
    {
        if (!_idleUnloadEnabled || _disposed) return;

        // Cloud engines hold no local memory — nothing worth unloading.
        if (_currentEngine?.CurrentProfile is { } p && p.IsCloud()) return;

        var due = TimeSpan.FromMinutes(Constants.ModelIdleUnloadMinutes);
        lock (_lock)
        {
            if (_disposed) return;
            if (_idleTimer == null)
                _idleTimer = new Timer(_ => OnIdleTimeout(), null, due, Timeout.InfiniteTimeSpan);
            else
                _idleTimer.Change(due, Timeout.InfiniteTimeSpan);
        }
    }

    private void StopIdleTimer()
    {
        lock (_lock)
        {
            _idleTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnIdleTimeout()
    {
        _ = UnloadForIdleAsync();
    }

    private async Task UnloadForIdleAsync()
    {
        try
        {
            await _loadSemaphore.WaitAsync();
            try
            {
                if (!_idleUnloadEnabled || _disposed) return;
                if (Volatile.Read(ref _activeTranscriptions) > 0) return; // in use — stays armed via TranscribeAsync's finally
                if (_currentEngine?.IsModelLoaded != true) return;
                if (_currentEngine.CurrentProfile is { } p && p.IsCloud()) return;

                var before = GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024);
                Log.Info($"Idle for {Constants.ModelIdleUnloadMinutes} min — unloading {_currentEngine.CurrentProfile} to free memory");

                lock (_lock)
                {
                    _currentEngine.Dispose();
                    _currentEngine = null;
                }
                _unloadedForIdle = true;

                // The model buffers are native (whisper.cpp) and freed on Dispose; a collection
                // here just tidies the managed wrappers and returns a truthful log number.
                GC.Collect();
                var after = GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024);
                Log.Info($"Model unloaded (managed heap {before} → {after} MB; native model memory returned to OS). Reloads automatically on next recording.");
            }
            finally
            {
                _loadSemaphore.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Idle model unload failed", ex);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_lock)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;

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
