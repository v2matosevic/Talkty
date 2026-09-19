using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Talkty.App.Models;
using Talkty.App.Services;

namespace Talkty.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private bool _disposed;
    private float _latestAudioLevel;
    private int _audioUpdatePending;
    private Task _historySaveTask = Task.CompletedTask;
    internal Task PendingHistorySave => _historySaveTask;
    private bool _historySaveSucceeded;
    private readonly RecordingRecoveryStore _recoveryStore;
    public ObservableCollection<RecoverableRecording> RecoverableRecordings { get; } = [];

    private readonly ISettingsService _settingsService;
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly ITranscriptionService _transcriptionService;
    private readonly IClipboardService _clipboardService;
    private readonly IUpdateService _updateService;
    private readonly IVolumeDuckingService? _volumeDuckingService;
    private readonly IAutoPasteService _autoPasteService;
    private readonly IPromptRefinementService? _promptRefinementService;
    private readonly IPromptFidelityService? _promptFidelityService;
    private readonly PromptClassifier? _promptClassifier;
    private readonly IVoiceCommandService? _voiceCommandService;

    /// <summary>Armed by the command hotkey for the NEXT recording only.</summary>
    private bool _pendingCommandMode;

    /// <summary>Whether the recording currently running is a command, not dictation.</summary>
    private bool _commandModeRecording;

    // Linked across a single recording -> transcription cycle. ESC cancels it mid-flight,
    // which aborts both the NAudio capture (if still recording) and the Whisper decode.
    private CancellationTokenSource? _transcriptionCts;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _modelProfileDisplay = "Low";

    [ObservableProperty]
    private bool _isListening;

    [ObservableProperty]
    private bool _isTranscribing;

    [ObservableProperty]
    private bool _isModelLoaded;

    [ObservableProperty]
    private bool _isModelLoading;

    [ObservableProperty]
    private string _backendInfo = "";

    [ObservableProperty]
    private float _audioLevel;

    /// <summary>
    /// When true, the completed transcription is expanded into a structured coding-agent prompt
    /// before output. Mirrored from the overlay "Prompting" toggle; resets each recording.
    /// </summary>
    [ObservableProperty]
    private bool _promptMode;

    [ObservableProperty]
    private ObservableCollection<TranscriptionHistoryItem> _history = [];

    // Update notification
    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private string _latestVersion = "";

    [ObservableProperty]
    private string _updateDownloadUrl = "";

    public string CurrentVersion => _updateService.CurrentVersion;

    public event EventHandler? RequestShowOverlay;
    public event EventHandler? RequestHideOverlay;

    /// <summary>
    /// Progress of a spoken command, for the pill. Raised with his own words
    /// first, then with whatever the daemon says happened, so the overlay can
    /// show the whole thing instead of vanishing into a toast.
    /// </summary>
    public event EventHandler<CommandProgressEventArgs>? CommandProgress;
    public event EventHandler? RequestShowSettings;
    public event EventHandler<ToastEventArgs>? RequestShowToast;
    public event EventHandler? RecordingStarted;
    public event EventHandler? RecordingStopped;

    public MainViewModel(
        ISettingsService settingsService,
        IAudioCaptureService audioCaptureService,
        ITranscriptionService transcriptionService,
        IClipboardService clipboardService,
        IUpdateService? updateService = null,
        IVolumeDuckingService? volumeDuckingService = null,
        IAutoPasteService? autoPasteService = null,
        IPromptRefinementService? promptRefinementService = null,
        RecordingRecoveryStore? recoveryStore = null,
        IPromptFidelityService? promptFidelityService = null,
        IVoiceCommandService? voiceCommandService = null,
        PromptClassifier? promptClassifier = null)
    {
        Log.Info("MainViewModel constructor starting");

        _settingsService = settingsService;
        _voiceCommandService = voiceCommandService;
        _recoveryStore = recoveryStore ?? new RecordingRecoveryStore();
        foreach (var recording in _recoveryStore.Load()) RecoverableRecordings.Add(recording);
        _audioCaptureService = audioCaptureService;
        _transcriptionService = transcriptionService;
        _clipboardService = clipboardService;
        _updateService = updateService ?? new UpdateService();
        _volumeDuckingService = volumeDuckingService;
        _autoPasteService = autoPasteService ?? throw new ArgumentNullException(nameof(autoPasteService));
        _promptRefinementService = promptRefinementService;
        _promptFidelityService = promptFidelityService;
        _promptClassifier = promptClassifier;
        if (_promptFidelityService != null)
            _promptFidelityService.ConcernRaised += OnFidelityConcernRaised;

        _audioCaptureService.AudioLevelChanged += OnAudioLevelChanged;
        Log.Debug("AudioLevelChanged event handler attached");

        // Fire-and-forget startup load. Top-level try/catch inside the method is the only
        // safety net — don't convert to await; the constructor can't be async.
        _ = LoadSettingsAndModelAsync();
        Log.Info("MainViewModel constructor completed");
    }

    private async Task LoadSettingsAndModelAsync()
    {
        try
        {
            Log.Info("LoadSettingsAndModel starting");

            // Settings are already loaded — MainWindow calls Load() before constructing this
            // ViewModel. Re-loading here was a redundant disk read + deserialize at startup.
            var settings = _settingsService.Settings;
            _transcriptionService.SetCloudFallback(settings.CloudFallbackModel);
            Log.Debug($"Settings loaded. ModelProfile: {settings.ModelProfile}, Mic: {settings.SelectedMicrophoneId ?? "default"}");

            ModelProfileDisplay = settings.ModelProfile.GetDisplayName();

            if (!string.IsNullOrEmpty(settings.SelectedMicrophoneId))
            {
                _audioCaptureService.SelectDevice(settings.SelectedMicrophoneId);
                Log.Debug($"Audio device selected: {settings.SelectedMicrophoneId}");
            }

            // Initialize volume ducking level from settings
            if (_volumeDuckingService != null)
            {
                _volumeDuckingService.DuckLevel = settings.VolumeDuckLevel;
                Log.Debug($"Volume duck level set to: {settings.VolumeDuckLevel:P0}");
            }

            // Load persisted history
            LoadPersistedHistory();

            // Pre-set vocabulary prompt so the processor is built with it from the start
            // (avoids a processor rebuild on the first transcription). English-only — must
            // match the per-transcription gate or the first transcription forces a rebuild.
            _transcriptionService.SetVocabularyPrompt(VocabularyPromptBuilder.Build(settings));

            // Pre-set the language too — same reason as the vocabulary prompt: the processor
            // must be built with the language actually used at transcription time, or the
            // first transcription after every (re)load pays a full processor rebuild.
            _transcriptionService.SetLanguageHint(settings.AutoDetectLanguage ? "auto" : settings.Language);

            // Forward the decrypted cloud API key to the transcription engine AND the prompt
            // refiner — both call OpenRouter.
            var cloudKey = ApiKeyProtector.Unprotect(settings.OpenRouterApiKeyEncrypted);
            _transcriptionService.SetCloudApiKey(cloudKey);
            _promptRefinementService?.SetApiKey(cloudKey);
            _promptRefinementService?.SetModel(settings.PromptingModel);

            // The fidelity check reuses the SAME OpenRouter connection — no second credential.
            if (_promptFidelityService != null)
            {
                _promptFidelityService.SetApiKey(cloudKey);
                _promptFidelityService.Mode = settings.PromptFidelity;
            }
            _promptClassifier?.SetApiKey(cloudKey);

            _transcriptionService.SetIdleUnload(settings.UnloadModelWhenIdle);

            await LoadModelAsync(settings.ModelProfile, settings.UseGpu);

            // Check for updates in background (non-blocking)
            _ = CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            Log.Error("LoadSettingsAndModel failed — app may be in degraded state", ex);
            StatusText = "Startup error — check settings";
        }
    }

    public async Task CheckForUpdatesAsync()
    {
        try
        {
            var updateInfo = await _updateService.CheckForUpdatesAsync();
            if (updateInfo?.UpdateAvailable == true)
            {
                UpdateAvailable = true;
                LatestVersion = updateInfo.LatestVersion;
                UpdateDownloadUrl = updateInfo.DownloadUrl;
                Log.Info($"Update available: v{updateInfo.LatestVersion}");
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Update check failed: {ex.Message}");
        }
    }

    private void LoadPersistedHistory()
    {
        try
        {
            var entries = _settingsService.LoadHistory();
            foreach (var entry in entries.Take(Constants.MaxHistoryEntries))
            {
                History.Add(new TranscriptionHistoryItem
                {
                    Text = entry.Text,
                    RawTranscription = entry.RawTranscription,
                    Timestamp = entry.Timestamp,
                    Duration = TimeSpan.FromSeconds(entry.DurationSeconds)
                });
            }
            Log.Info($"Loaded {History.Count} history entries from disk");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load history from disk", ex);
        }
    }

    private void QueueHistorySave()
    {
        // Snapshot on the UI thread, then persist in order. Enumerating History on a
        // worker could race with Clear/Delete, and older writes could resurrect entries.
        var entries = History.Select(h => new TranscriptionHistoryEntry
            {
                Text = h.Text,
                RawTranscription = h.RawTranscription,
                Timestamp = h.Timestamp,
                DurationSeconds = h.Duration.TotalSeconds
            }).ToList();
        _historySaveTask = _historySaveTask.ContinueWith(_ =>
        {
            try { _settingsService.SaveHistory(entries); _historySaveSucceeded = true; }
            catch (Exception ex) { _historySaveSucceeded = false; Log.Error("Failed to save history to disk", ex); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    public async Task LoadModelAsync(ModelProfile profile, bool useGpu = false)
    {
        Log.Info($"LoadModelAsync starting for profile: {profile}, UseGpu: {useGpu}");

        // Cloud profiles have no local file, no GPU, no download — handle separately.
        if (profile.IsCloud())
        {
            await LoadCloudModelAsync(profile);
            return;
        }

        var modelPath = _settingsService.GetModelPath(profile);
        Log.Debug($"Model path: {modelPath}");

        if (!_settingsService.ModelExists(profile))
        {
            Log.Warning($"Model file not found: {profile.GetModelFileName()}");
            StatusText = $"Model not found: {profile.GetModelFileName()}";
            BackendInfo = $"Please place model in: {_settingsService.GetModelsDirectory()}";
            IsModelLoaded = false;
            return;
        }

        IsModelLoading = true;
        StatusText = useGpu ? "Loading model (GPU)..." : "Loading model...";
        Log.Info($"Loading model... GPU: {useGpu}");

        try
        {
            var startTime = DateTime.Now;
            IsModelLoaded = await _transcriptionService.LoadModelAsync(profile, modelPath, useGpu);
            var elapsed = DateTime.Now - startTime;

            if (IsModelLoaded)
            {
                Log.Info($"Model loaded successfully in {elapsed.TotalSeconds:F1}s. Backend: {_transcriptionService.BackendInfo}");
                StatusText = "Ready";
                BackendInfo = _transcriptionService.BackendInfo ?? "";
                ModelProfileDisplay = profile.GetDisplayName();

                // Show success toast with backend info
                var backendType = (BackendInfo.Contains("CUDA", StringComparison.OrdinalIgnoreCase) ||
                                   BackendInfo.Contains("Vulkan", StringComparison.OrdinalIgnoreCase)) ? "GPU" : "CPU";
                RequestShowToast?.Invoke(this, new ToastEventArgs
                {
                    Message = $"Model loaded: {profile.GetDisplayName()} ({backendType})",
                    Type = ToastType.Success,
                    DurationMs = 3000
                });
            }
            else
            {
                Log.Error($"Failed to load model. Info: {_transcriptionService.BackendInfo}");
                StatusText = "Failed to load model";
                BackendInfo = _transcriptionService.BackendInfo ?? "";

                // Show error toast
                RequestShowToast?.Invoke(this, new ToastEventArgs
                {
                    Message = "Failed to load model - check settings",
                    Type = ToastType.Warning,
                    DurationMs = 4000
                });
            }
        }
        finally
        {
            IsModelLoading = false;
        }
    }

    /// <summary>
    /// Loads a cloud (OpenRouter) profile: no download or file check — just validates the
    /// API key and marks the remote model ready. Requires an OpenRouter key in Settings.
    /// </summary>
    private async Task LoadCloudModelAsync(ModelProfile profile)
    {
        Log.Info($"LoadCloudModelAsync for {profile} ({profile.GetOpenRouterModelId()})");

        var apiKey = ApiKeyProtector.Unprotect(_settingsService.Settings.OpenRouterApiKeyEncrypted);
        _transcriptionService.SetCloudApiKey(apiKey);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Log.Warning("Cloud model selected but no OpenRouter API key configured");
            StatusText = "Add your OpenRouter API key in Settings";
            BackendInfo = "Cloud model needs an API key";
            IsModelLoaded = false;
            RequestShowToast?.Invoke(this, new ToastEventArgs
            {
                Message = "Add your OpenRouter API key in Settings to use cloud models",
                Type = ToastType.Warning,
                DurationMs = 5000
            });
            return;
        }

        IsModelLoading = true;
        StatusText = "Connecting to cloud...";
        try
        {
            IsModelLoaded = await _transcriptionService.LoadModelAsync(profile, "", false);

            if (IsModelLoaded)
            {
                Log.Info($"Cloud model ready: {_transcriptionService.BackendInfo}");
                StatusText = "Ready";
                BackendInfo = _transcriptionService.BackendInfo ?? "";
                ModelProfileDisplay = profile.GetDisplayName();
                RequestShowToast?.Invoke(this, new ToastEventArgs
                {
                    Message = $"Cloud model ready: {profile.GetDisplayName()}",
                    Type = ToastType.Success,
                    DurationMs = 3000
                });
            }
            else
            {
                Log.Error($"Cloud model failed to load. Info: {_transcriptionService.BackendInfo}");
                StatusText = "Failed to connect cloud model";
                BackendInfo = _transcriptionService.BackendInfo ?? "";
                RequestShowToast?.Invoke(this, new ToastEventArgs
                {
                    Message = "Cloud model unavailable — check your API key",
                    Type = ToastType.Warning,
                    DurationMs = 4000
                });
            }
        }
        finally
        {
            IsModelLoading = false;
        }
    }

    [RelayCommand]
    public void ToggleListening()
    {
        // The command hotkey arms the NEXT recording. Consume that arming here so a
        // refused start (still transcribing, model loading, no model) cannot leak
        // command mode into the next ordinary dictation.
        var commandRequested = _pendingCommandMode;
        _pendingCommandMode = false;

        Log.Info($"ToggleListening called. IsListening: {IsListening}, IsTranscribing: {IsTranscribing}, IsModelLoaded: {IsModelLoaded}, IsModelLoading: {IsModelLoading}");

        if (IsTranscribing)
        {
            Log.Debug("Ignoring - already transcribing");
            return;
        }

        if (IsModelLoading)
        {
            Log.Debug("Ignoring - model is loading");
            StatusText = "Please wait - loading model...";
            return;
        }

        if (!IsListening && _audioCaptureService.IsRecording)
        {
            ShowWarning("Stop the microphone test in Settings before recording.");
            return;
        }

        if (!IsModelLoaded)
        {
            Log.Warning("Model not loaded - cannot start listening");
            StatusText = "Choose a model in Settings";
            RequestShowSettings?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (IsListening)
        {
            Log.Info("Stopping listening and starting transcription");
            _ = StopListeningAndTranscribeAsync();
        }
        else
        {
            Log.Info(commandRequested ? "Starting listening (command mode)" : "Starting listening");
            _commandModeRecording = commandRequested;
            _ = StartListeningAsync();
        }
    }

    /// <summary>
    /// The command hotkey (Alt+W). It records exactly like dictation; the only
    /// difference is where the transcript goes when it finishes. Pressing it while a
    /// recording is already running simply stops that recording, in whatever mode it
    /// started, because the mode belongs to the recording and not to the key.
    /// </summary>
    [RelayCommand]
    public void ToggleCommandListening()
    {
        if (!IsListening) _pendingCommandMode = true;
        ToggleListening();
    }

    /// <summary>
    /// Test seam: mark the recording currently running as a command. ToggleListening
    /// does this from the hotkey's arming; tests start the recorder directly.
    /// </summary>
    internal void MarkRecordingAsCommand() => _commandModeRecording = true;

    /// <summary>Test seam: whether the NEXT recording is armed as a command.</summary>
    internal bool IsCommandModeArmed => _pendingCommandMode;

    /// <summary>
    /// Cancels the current recording without transcribing.
    /// Called when user presses ESC during recording.
    /// </summary>
    [RelayCommand]
    public void CancelRecording()
    {
        if (!IsListening && !IsTranscribing)
        {
            Log.Debug("CancelRecording called but not active");
            return;
        }

        Log.Info(">>> CANCELLED (ESC) <<<");

        try
        {
            // Cancel the token — aborts in-flight Whisper decode if transcribing
            try { _transcriptionCts?.Cancel(); }
            catch (ObjectDisposedException) { /* already completed */ }

            // A fidelity check runs after delivery on its own token; ESC stops that too.
            _promptFidelityService?.CancelPending();

            // Stop recording without getting audio (no-op if already stopped)
            if (IsListening)
            {
                _audioCaptureService.StopRecording();
            }
            IsListening = false;

            // Restore volume if ducked
            if (_settingsService.Settings.DuckVolumeWhileRecording && _volumeDuckingService != null)
            {
                Log.Debug("Restoring volume after cancel");
                _ = _volumeDuckingService.RestoreAsync();
            }

            StatusText = "Cancelled";

            // Hide overlay
            RequestHideOverlay?.Invoke(this, EventArgs.Empty);
            RecordingStopped?.Invoke(this, EventArgs.Empty);

            // Reset status after short delay
            Task.Delay(Constants.StatusResetDelayMs).ContinueWith(_ =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (!IsListening && !IsTranscribing)
                    {
                        StatusText = "Ready";
                    }
                });
            });
        }
        catch (Exception ex)
        {
            Log.Error("Failed to cancel recording", ex);
            IsListening = false;
            StatusText = "Ready";
            RecordingStopped?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task StartListeningAsync()
    {
        bool volumeDucked = false;
        try
        {

            // Duck system volume if enabled
            if (_settingsService.Settings.DuckVolumeWhileRecording && _volumeDuckingService != null)
            {
                Log.Debug("Ducking volume before recording");
                await _volumeDuckingService.DuckAsync();
                volumeDucked = true;
            }

            // Fresh cancellation source for this recording -> transcription cycle.
            // Disposed by the transcription path or CancelRecording.
            _transcriptionCts?.Dispose();
            _transcriptionCts = new CancellationTokenSource();

            // A concern about the previous prompt must not arrive over the next one.
            _promptFidelityService?.CancelPending();

            Log.Debug("Calling AudioCaptureService.StartRecording()");
            _audioCaptureService.StartRecording();

            // If the model was unloaded while idle, start reloading NOW so it overlaps with
            // the user speaking — by stop time it's usually ready and the reload costs no
            // perceived latency. TranscribeAsync awaits this same load if it's still going.
            _ = _transcriptionService.EnsureModelLoadedAsync();
            // Cloud models: open the connection and warm the audio encoder during speech too.
            _transcriptionService.PrewarmCloud();

            IsListening = true;
            StatusText = "Listening...";
            Log.Info("Recording started - raising RequestShowOverlay");
            RequestShowOverlay?.Invoke(this, EventArgs.Empty);
            RecordingStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start recording", ex);
            StatusText = $"Recording error: {ex.Message}";

            // Restore volume if we ducked it before the error
            if (volumeDucked && _volumeDuckingService != null)
            {
                Log.Debug("Restoring volume after recording start failure");
                _ = _volumeDuckingService.RestoreAsync();
            }
        }
    }

    internal async Task StopListeningAndTranscribeAsync(RecoverableRecording? retry = null)
    {
        var cycle = _transcriptionCts;
        var cancellationToken = cycle?.Token ?? default;
        // The mode belongs to the recording that just ended. Read it once and clear it,
        // so a recovery replay of old audio is never treated as a command.
        var commandMode = _commandModeRecording && retry == null;
        _commandModeRecording = false;
        RecoverableRecording? recovery = retry;
        try
        {
            // Capture the foreground window NOW (at stop time) — this is the app
            // the user is currently looking at and wants to paste into.
            if (retry == null) _autoPasteService.CaptureTargetWindow();
            var commandTarget = commandMode ? _autoPasteService.CapturedWindow : null;

            // Snapshot the clipboard BEFORE anything (incl. the streamed first segment)
            // overwrites it, so it can be restored after a successful auto-paste.
            string? clipboardToRestore = null;
            if (_settingsService.Settings.AutoPaste && _settingsService.Settings.RestoreClipboardAfterPaste)
            {
                clipboardToRestore = _clipboardService.GetTextOrNull();
            }

            // Claim foreground privilege IMMEDIATELY on the UI thread.
            // Windows only grants SetForegroundWindow permission to the thread
            // that last received user input (our hotkey). If we wait until after
            // transcription (~1s later), the privilege expires and paste fails.
            if (retry == null) _autoPasteService.ClaimForegroundPrivilege();

            // Restore system volume if ducking is enabled (always try, service handles "not ducked" case)
            if (_settingsService.Settings.DuckVolumeWhileRecording && _volumeDuckingService != null)
            {
                Log.Debug("Restoring volume after recording");
                _ = _volumeDuckingService.RestoreAsync(); // Fire-and-forget, don't block transcription
            }

            IsListening = false;
            IsTranscribing = true;
            StatusText = "Transcribing...";
            RecordingStopped?.Invoke(this, EventArgs.Empty);

            // Stop recording AND wait for NAudio's in-flight buffers to be delivered.
            // Without this flush, the last 200-400ms of speech is lost — exactly "the last
            // part of what I said" in the user's transcript.
            Log.Debug("Calling AudioCaptureService.StopRecordingAndFlushAsync()");
            var flushed = retry != null || await _audioCaptureService.StopRecordingAndFlushAsync(Constants.RecordingFlushTimeoutMs);
            cancellationToken.ThrowIfCancellationRequested();
            if (!flushed)
                ShowWarning("The microphone did not finish cleanly. The end of this recording may be missing.");

            Log.Debug("Getting recorded audio samples");
            var audioSamples = retry?.Samples ?? _audioCaptureService.GetRecordedAudioAsFloat();
            Log.Info($"Audio samples: {audioSamples.Length} ({audioSamples.Length / (float)Constants.SampleRate:F1}s at 16kHz)");

            if (audioSamples.Length == 0)
            {
                Log.Warning("No audio recorded!");
                StatusText = "No audio recorded";
                ShowWarning("No audio was recorded. Check your microphone in Settings.");
                IsTranscribing = false;
                RequestHideOverlay?.Invoke(this, EventArgs.Empty);
                return;
            }

            // Skip digital silence from muted/disconnected devices without risking quiet speech.
            if (Array.TrueForAll(audioSamples, sample => sample == 0f))
            {
                StatusText = "No audio detected";
                ShowWarning("No audio was detected. Check that your microphone is not muted.");
                return;
            }

            // Trim leading/trailing silence — reduces audio Whisper must process (10-25% faster)
            audioSamples = TrimSilence(audioSamples);

            if (recovery == null && _settingsService.Settings.ModelProfile.IsCloud())
            {
                recovery = new RecoverableRecording
                {
                    Samples = audioSamples, PromptMode = PromptMode,
                    Language = _settingsService.Settings.AutoDetectLanguage ? "auto" : _settingsService.Settings.Language
                };
                // Keep an in-memory copy even if disk is full; do not send unprotected audio.
                RecoverableRecordings.Insert(0, recovery);
            }
            if (recovery != null)
            {
                await Task.Run(() => _recoveryStore.Save(recovery));
                cancellationToken.ThrowIfCancellationRequested();
            }

            Log.Info("Starting transcription...");
            var startTime = DateTime.Now;
            var language = recovery?.Language ?? (_settingsService.Settings.AutoDetectLanguage ? "auto" : _settingsService.Settings.Language);

            // Use the same bounded, saved vocabulary at startup and on each recording.
            var vocabularyPrompt = VocabularyPromptBuilder.Build(_settingsService.Settings);
            if (vocabularyPrompt != null)
            {
                Log.Debug($"Vocabulary prompt: {vocabularyPrompt.Length} chars");
            }
            // Cloud models that take keyword hints get the same saved words as a list.
            var vocabularyTerms = VocabularyPromptBuilder.BuildCloudTerms(_settingsService.Settings);

            // Load text replacements for post-processing (applied after Whisper output)
            var textReplacements = _settingsService.Settings.UseCustomVocabulary
                ? _settingsService.Settings.TextReplacements
                : null;

            // Streaming callback: copy first segment to clipboard immediately (before full transcription completes).
            // This lets clipboard-only users paste sooner. Auto-paste waits for full text.
            Action<string>? onFirstSegment = null;
            // Partial clipboard writes help manual paste only. In auto-paste/prompt mode
            // they overwrite the user's clipboard even when the operation is cancelled.
            if (_settingsService.Settings.CopyToClipboard && !_settingsService.Settings.AutoPaste && !PromptMode)
            {
                onFirstSegment = (text) =>
                {
                    try
                    {
                        // Strip hallucinations first — otherwise "Thanks for watching" can
                        // hit the clipboard before the full-pass clean runs.
                        text = TextPostProcessor.StripHallucinations(text);

                        // Apply post-processing to streamed segment too — same treatment as the
                        // full pass, so an early paste doesn't differ from the final text.
                        if (textReplacements is { Count: > 0 })
                            text = TextPostProcessor.ApplyReplacements(text, textReplacements);
                        text = TextPostProcessor.CleanupPunctuation(text);

                        if (string.IsNullOrWhiteSpace(text))
                        {
                            Log.Debug("First segment empty after cleanup — skipping clipboard");
                            return;
                        }

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (!cancellationToken.IsCancellationRequested)
                                _clipboardService.SetText(text);
                        });
                        Log.Info($"First segment → clipboard ({text.Length} chars)");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"First segment clipboard copy failed: {ex.Message}");
                    }
                };
            }

            var result = await _transcriptionService.TranscribeAsync(audioSamples, language, cancellationToken, onFirstSegment, vocabularyPrompt, vocabularyTerms);
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = DateTime.Now - startTime;

            Log.Info($"Transcription completed in {elapsed.TotalSeconds:F1}s. Success: {result.Success}");

            if (result.Success && !string.IsNullOrWhiteSpace(result.Text))
            {
                // Strip Whisper hallucinations (e.g., "Thank you for watching", "[MUSIC]")
                result.Text = TextPostProcessor.StripHallucinations(result.Text);

                // Apply deterministic text replacements (cloud→Claude, etc.)
                if (textReplacements is { Count: > 0 })
                {
                    var original = result.Text;
                    var corrected = TextPostProcessor.ApplyReplacements(original, textReplacements);
                    if (corrected != original)
                    {
                        result.Text = corrected;
                        Log.Info($"Post-processing: \"{original}\" → \"{corrected}\"");
                    }
                }

                // Clean up punctuation: merge false sentence breaks, normalize spacing
                result.Text = TextPostProcessor.CleanupPunctuation(result.Text);
                if (string.IsNullOrWhiteSpace(result.Text))
                {
                    StatusText = "No speech detected";
                    ShowWarning("No speech was detected. Nothing was copied.");
                    return;
                }

                Log.Info($"Transcribed text ({result.Text.Length} chars): \"{result.Text}\"");

                // Command mode: this dictation is an instruction for the machine, not
                // text for the cursor. It goes to the local daemon and stops here —
                // Prompting must never rewrite an instruction, and the clipboard and
                // auto-paste must not fire. If nothing is listening we fall through to
                // ordinary dictation, so a sentence is never lost to a stopped daemon.
                if (commandMode)
                {
                    // His words go on the pill before the daemon has said
                    // anything, so the wait is visibly about his sentence.
                    ReportCommand(result.Text, CommandStage.Sending, "Sending…");
                    var dispatch = await DispatchCommandAsync(result.Text, cancellationToken, commandTarget);
                    if (dispatch.Outcome != VoiceCommandOutcome.NotReached)
                    {
                        RecordCommandInHistory(result, recovery);
                        StatusText = dispatch.Message;

                        if (dispatch.IsWorking)
                        {
                            // Accepted, not finished. Keep the pill on it and
                            // replace this line when the goal actually ends.
                            ReportCommand(result.Text, CommandStage.Working, dispatch.Message);
                            FollowCommandGoal(dispatch.GoalId!, result.Text);
                            return;
                        }

                        var stage = dispatch.Outcome == VoiceCommandOutcome.Uncertain || !dispatch.Ok
                            ? CommandStage.Failed
                            : CommandStage.Succeeded;
                        ReportCommand(result.Text, stage, dispatch.Message);
                        return;
                    }

                    Log.Info($"Command not delivered ({dispatch.Message}) — falling back to dictation");
                    ReportCommand(result.Text, CommandStage.None, null);
                    ShowWarning($"{dispatch.Message}. Copied the text instead.");
                }

                // Prompt mode: expand the transcription into a structured coding-agent prompt
                // before it hits the clipboard/paste. Falls back to the raw text on any failure.
                // When a prompt IS generated, keep the original transcription so history shows both.
                string? rawTranscriptionForHistory = null;
                if (PromptMode)
                {
                    if (_promptRefinementService?.IsConfigured == true)
                    {
                        var rawTranscription = result.Text;

                        // Classify BEFORE spending a generation call. On a genuinely one-line ask
                        // this can skip refinement entirely, which makes the common case faster
                        // rather than slower. Every failure path returns today's behaviour.
                        var plan = await PlanRefinementAsync(rawTranscription, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();

                        if (plan.Decision == PromptPlanDecision.SkipRefinement)
                        {
                            Log.Info($"Prompt planning: dictation is already a prompt " +
                                     $"(kind={plan.Kind}, complexity={plan.Complexity:F2}) — skipping refinement");
                            StatusText = "Ready to paste";
                        }

                        else
                        {
                            StatusText = "Refining prompt...";
                            var refined = await _promptRefinementService.RefineAsync(
                                result.Text, cancellationToken,
                                PromptClassifier.HintFor(plan), plan.WantsQualityModel);
                            // Refiners can return null on cancellation. Never treat that as
                            // permission to paste the raw transcript.
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!string.IsNullOrWhiteSpace(refined))
                            {
                                Log.Info($"Prompt mode: expanded into agent prompt ({result.Text.Length} → {refined.Length} chars)");
                                result.Text = refined;
                                rawTranscriptionForHistory = rawTranscription;

                                // Fidelity check: did the rewrite keep every instruction, value and
                                // prohibition? Deliberately NOT awaited — it must never sit between the
                                // user and their clipboard. It owns its own deadline, budget and
                                // cancellation, and in Record only it shows nothing at all.
                                StartFidelityCheck(rawTranscription, refined);
                            }
                            else
                            {
                                Log.Warning("Prompt refinement returned nothing — using raw transcription");
                                // The user asked for a prompt and is silently getting raw dictation —
                                // say so (unless they cancelled via ESC, where silence is expected).
                                if (cancellationToken.IsCancellationRequested == false)
                                {
                                    RequestShowToast?.Invoke(this, new ToastEventArgs
                                    {
                                        Message = _promptRefinementService.LastError
                                                  ?? "Prompting failed — copied the raw transcription instead",
                                        Type = ToastType.Warning,
                                        DurationMs = 5000
                                    });
                                }
                            }
                        }
                    }
                    else
                    {
                        Log.Warning("Prompt mode on but no OpenRouter key — skipping refinement");
                        RequestShowToast?.Invoke(this, new ToastEventArgs
                        {
                            Message = "Add your OpenRouter API key in Settings to use Prompting",
                            Type = ToastType.Warning,
                            DurationMs = 4000
                        });
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                StatusText = "Saved to history";
                if (_settingsService.Settings.CopyToClipboard)
                {
                    // Update clipboard with full text (overwrites first-segment partial if multi-segment)
                    Log.Debug("Copying full text to clipboard");
                    bool clipboardSuccess = false;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        clipboardSuccess = _clipboardService.SetText(result.Text);
                        Log.Debug($"Clipboard copy result: {clipboardSuccess}");
                    });

                    StatusText = clipboardSuccess ? "Copied to clipboard" : "Saved to history; clipboard unavailable";
                    if (!clipboardSuccess)
                        ShowWarning("Could not copy the text. Your transcription is saved in History.");

                    if (retry == null && _settingsService.Settings.AutoPaste && clipboardSuccess)
                    {
                        // Run paste on thread pool so Thread.Sleep calls don't block UI thread.
                        // Overlay stays visible during paste — hiding it would cause focus changes.
                        Log.Debug("Auto-pasting at cursor");
                        var textForClipboard = result.Text;
                        var clipboardReadyForPaste = true;
                        var pasteOutcome = await Task.Run(() =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            return _autoPasteService.PasteToTargetWindow(ensureClipboardText: () =>
                            {
                                // Re-set clipboard right before Ctrl+V — focus switching can
                                // cause some apps to clear or claim the clipboard.
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    clipboardReadyForPaste = _clipboardService.SetText(textForClipboard);
                                    if (!clipboardReadyForPaste)
                                        throw new InvalidOperationException("Clipboard became unavailable before paste.");
                                });
                            }, cancellationToken);
                        });
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!clipboardReadyForPaste)
                        {
                            StatusText = "Saved to history; clipboard unavailable";
                            ShowWarning("The clipboard became unavailable before paste. Your transcription is saved in History.");
                        }
                        else if (pasteOutcome != PasteOutcome.Pasted)
                        {
                            // The text is safe on the clipboard — say WHY it didn't land
                            // instead of leaving the user staring at an unchanged window.
                            var reason = pasteOutcome switch
                            {
                                PasteOutcome.TargetElevated =>
                                    "That app runs as administrator, so Windows blocks auto-paste — press Ctrl+V to paste.",
                                PasteOutcome.NoTarget =>
                                    "The window to paste into is gone — text is on the clipboard (Ctrl+V).",
                                PasteOutcome.FocusRestoreFailed =>
                                    "Couldn't focus the target window — text is on the clipboard (Ctrl+V).",
                                _ =>
                                    "Auto-paste failed — text is on the clipboard (Ctrl+V).",
                            };
                            RequestShowToast?.Invoke(this, new ToastEventArgs
                            {
                                Message = reason,
                                Type = ToastType.Warning,
                                DurationMs = 5000
                            });
                        }
                        else if (clipboardToRestore != null)
                        {
                            // Give the target app a beat to consume WM_PASTE, then hand the
                            // user their original clipboard back.
                            await Task.Delay(Constants.ClipboardRestoreDelayMs);
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                // Only restore if the clipboard still holds OUR transcription.
                                // If the user (or a rapid follow-up dictation) already changed
                                // it, restoring would clobber the newer content.
                                if (_clipboardService.GetTextOrNull() == textForClipboard)
                                {
                                    _clipboardService.SetText(clipboardToRestore);
                                    Log.Info("Previous clipboard content restored after paste");
                                }
                                else
                                {
                                    Log.Debug("Clipboard changed since paste — skipping restore");
                                }
                            });
                        }
                    }
                }

                // History update + disk persist — fire-and-forget, don't block status reset
                Application.Current.Dispatcher.Invoke(() =>
                {
                    History.Insert(0, new TranscriptionHistoryItem
                    {
                        Text = result.Text,
                        RawTranscription = rawTranscriptionForHistory,
                        Timestamp = recovery?.Timestamp ?? result.Timestamp,
                        Duration = result.Duration
                    });

                    if (History.Count > Constants.MaxHistoryEntries)
                    {
                        History.RemoveAt(History.Count - 1);
                    }
                    QueueHistorySave();
                });
                await PendingHistorySave;
                if (recovery != null && _historySaveSucceeded)
                {
                    _recoveryStore.Delete(recovery.Id);
                    RecoverableRecordings.Remove(recovery);
                    recovery = null;
                }
                else if (recovery != null)
                {
                    recovery.Error = "Text could not be saved to history. Recording kept for retry.";
                    ShowWarning(recovery.Error);
                }
                if (result.UsedFallback is { } fallback)
                    RequestShowToast?.Invoke(this, new ToastEventArgs
                    {
                        Message = $"Transcribed using backup: {fallback.GetDisplayName()}",
                        Type = ToastType.Success, DurationMs = 5000
                    });
            }
            else
            {
                // Surface cancellation distinctly from generic failures
                if (_transcriptionCts?.IsCancellationRequested == true)
                {
                    Log.Info("Transcription cancelled via ESC");
                    StatusText = "Cancelled";
                }
                else
                {
                    Log.Warning($"Transcription failed or empty. Error: {result.ErrorMessage}");
                    if (recovery != null)
                    {
                        recovery.Error = result.ErrorMessage ?? "Transcription failed.";
                        await Task.Run(() => _recoveryStore.Save(recovery));
                    }
                    StatusText = result.ErrorMessage ?? "Transcription failed";
                    // The app usually lives hidden in the tray — StatusText alone is invisible
                    // there. Toast so the user knows nothing reached the clipboard.
                    RequestShowToast?.Invoke(this, new ToastEventArgs
                    {
                        Message = (result.ErrorMessage ?? "Transcription failed") +
                            (recovery != null ? " Recording saved. Open Talkty to retry." : " Nothing was copied."),
                        Type = ToastType.Warning,
                        DurationMs = 5000
                    });
                }
            }

            // Hide overlay AFTER auto-paste completes — hiding before paste causes focus loss
            IsTranscribing = false;
            RequestHideOverlay?.Invoke(this, EventArgs.Empty);

            // Brief pause so user sees "Copied to clipboard" before resetting
            await Task.Delay(100);
            if (!IsListening && !IsTranscribing)
            {
                StatusText = RecoverableRecordings.Count > 0 ? "Recording saved. Retry in Talkty." : "Ready";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.Info("Transcription cancelled; output discarded");
            if (recovery != null) recovery.Error = "Transcription cancelled. Recording kept for retry.";
            StatusText = "Cancelled";
        }
        catch (Exception ex)
        {
            Log.Error("StopListeningAndTranscribe failed", ex);
            if (recovery != null) recovery.Error = "Could not finish or save to disk. Retry before closing Talkty.";
            ShowWarning(recovery != null ? recovery.Error : "Transcription could not finish. Please try again.");
            StatusText = $"Error: {ex.Message}";
            IsTranscribing = false;
            RequestHideOverlay?.Invoke(this, EventArgs.Empty);

            // Ensure volume is restored even on error
            if (_settingsService.Settings.DuckVolumeWhileRecording && _volumeDuckingService != null)
            {
                Log.Debug("Restoring volume after transcription error");
                _ = _volumeDuckingService.RestoreAsync();
            }
        }
        finally
        {
            // Covers cancellation and every early return, including empty cleaned output.
            if (ReferenceEquals(_transcriptionCts, cycle))
            {
                if (IsTranscribing)
                {
                    IsTranscribing = false;
                    RequestHideOverlay?.Invoke(this, EventArgs.Empty);
                }
                cycle?.Dispose();
                _transcriptionCts = null;
            }
        }
    }

    private void ShowWarning(string message) =>
        RequestShowToast?.Invoke(this, new ToastEventArgs
        {
            Message = message,
            Type = ToastType.Warning,
            DurationMs = 5000
        });

    /// <summary>
    /// Decides what to do with a dictation before the refinement model sees it. Returns today's
    /// behaviour unless planning is switched on AND the classifier answers confidently, so the
    /// worst case is one extra sub-second call that changes nothing.
    /// </summary>
    private async Task<PromptPlan> PlanRefinementAsync(string transcript, CancellationToken cancellationToken)
    {
        var mode = _settingsService.Settings.PromptPlanning;
        if (mode == PromptPlanning.Off || _promptClassifier?.IsConfigured != true)
            return PromptPlan.Default("planning_off");

        var plan = await _promptClassifier.ClassifyAsync(transcript, cancellationToken);

        // Hints mode gets the kind and the model choice but never loses a refinement: only Full
        // may hand back raw dictation instead of a generated prompt.
        if (mode != PromptPlanning.Full && plan.Decision == PromptPlanDecision.SkipRefinement)
            return plan with { Decision = PromptPlanDecision.Refine, Reason = "skip_not_enabled" };

        return plan;
    }

    /// <summary>
    /// Starts the optional prompt-fidelity check. Fire-and-forget by design: the prompt is already
    /// on its way to the clipboard, so the check adds zero latency to delivery and a failure of any
    /// kind leaves the established flow untouched.
    /// </summary>
    /// <summary>
    /// Hand one command to the local daemon. Never throws; the service turns every
    /// failure into an outcome the caller can act on.
    /// </summary>
    /// <summary>Tell the pill what this command is doing. Never throws into the pipeline.</summary>
    private void ReportCommand(string text, CommandStage stage, string? detail)
    {
        try
        {
            CommandProgress?.Invoke(this, new CommandProgressEventArgs
            {
                Text = text,
                Stage = stage,
                Detail = detail ?? string.Empty,
            });
        }
        catch (Exception ex)
        {
            Log.Warning($"Command progress could not be shown: {ex.Message}");
        }
    }

    /// <summary>
    /// Watch a goal the daemon is still working on and keep the pill honest
    /// about it. Deliberately detached from the recording cycle: the next Alt+W
    /// must not wait for a goal, and a goal must not end because he spoke again.
    /// </summary>
    private void FollowCommandGoal(string goalId, string spoken)
    {
        if (_voiceCommandService is null) return;
        var service = _voiceCommandService;
        _ = Task.Run(async () =>
        {
            var progress = new Progress<VoiceGoalUpdate>(update =>
            {
                if (!update.Finished)
                {
                    var step = update.Steps > 0 ? $"Working… {update.Steps} steps" : "Working…";
                    ReportCommand(spoken, CommandStage.Working, update.Detail ?? step);
                    return;
                }

                var detail = update.Detail ?? (update.Succeeded ? "Done." : $"Stopped: {update.Status}.");
                ReportCommand(spoken, update.Succeeded ? CommandStage.Succeeded : CommandStage.Failed, detail);
                if (!update.Succeeded)
                {
                    RequestShowToast?.Invoke(this, new ToastEventArgs
                    {
                        Message = detail,
                        Type = ToastType.Warning,
                        DurationMs = 6000,
                    });
                }
            });

            try { await service.FollowGoalAsync(goalId, progress, CancellationToken.None); }
            catch (Exception ex) { Log.Info($"Goal {goalId} follow ended: {ex.Message}"); }
        });
    }

    private async Task<VoiceCommandResult> DispatchCommandAsync(string text, CancellationToken cancellationToken, CapturedWindowInfo? targetWindow)
    {
        if (_voiceCommandService == null || !_voiceCommandService.IsConfigured)
        {
            return new VoiceCommandResult(VoiceCommandOutcome.NotReached,
                "Command mode is not configured");
        }

        StatusText = "Sending command...";
        return await _voiceCommandService.DispatchAsync(text, targetWindow?.ProcessName, cancellationToken, targetWindow);
    }

    /// <summary>
    /// A command still belongs in history: it is a thing he said, and when a command
    /// goes wrong the exact words are the first thing worth reading back.
    /// </summary>
    private void RecordCommandInHistory(TranscriptionResult result, RecoverableRecording? recovery)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            History.Insert(0, new TranscriptionHistoryItem
            {
                Text = result.Text,
                Timestamp = recovery?.Timestamp ?? result.Timestamp,
                Duration = result.Duration
            });
            if (History.Count > Constants.MaxHistoryEntries) History.RemoveAt(History.Count - 1);
            QueueHistorySave();
        });
    }

    private void StartFidelityCheck(string transcript, string rewrite)
    {
        var service = _promptFidelityService;
        if (service == null || service.Mode == PromptFidelityMode.Off) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await service.EvaluateAsync(transcript, rewrite);
            }
            catch (Exception ex)
            {
                Log.Warning($"Fidelity check threw: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Shows a fidelity concern. The text quoted back is the USER'S OWN dictation and the label is
    /// a fixed string chosen by code — the check reports what may be missing, it never claims the
    /// prompt is wrong and it never offers a replacement.
    /// </summary>
    private void OnFidelityConcernRaised(object? sender, FidelityConcernEventArgs e)
    {
        if (e.Concerns.Count == 0) return;

        var message = BuildConcernMessage(e.Concerns);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.InvokeAsync(() => RequestShowToast?.Invoke(this, new ToastEventArgs
        {
            Message = message,
            Type = ToastType.Warning,
            DurationMs = 8000
        }));
    }

    /// <summary>
    /// Assembles the concern message, bounded so it survives delivery as a Windows tray balloon.
    /// That is the normal delivery path — Talkty is in the tray while you dictate into another app
    /// — and the shell truncates balloon text at 256 characters silently, so a message that reads
    /// fine in the in-app toast would arrive cut in half where it actually matters.
    /// </summary>
    internal static string BuildConcernMessage(IReadOnlyList<FidelityConcern> concerns)
    {
        var quoteBudget = concerns.Count <= 1
            ? Constants.FidelityQuoteCharsSingle
            : Constants.FidelityQuoteCharsShared;

        var lines = concerns.Select(c =>
            string.IsNullOrWhiteSpace(c.SourceText)
                ? c.Label
                : $"{c.Label}: “{Shorten(c.SourceText, quoteBudget)}”");

        var message = "Prompt check — " + string.Join("  •  ", lines);

        // Belt as well as braces: the per-concern budget makes this rare, but the ceiling is what
        // actually guarantees nothing is silently cut by the shell.
        return message.Length <= Constants.FidelityToastMaxChars
            ? message
            : message[..(Constants.FidelityToastMaxChars - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// Trims a quoted clause to fit, breaking on a word boundary. Cutting mid-word ("at the moment
    /// a…") reads like a bug in the quote rather than a deliberate shortening of the user's own
    /// sentence.
    /// </summary>
    private static string Shorten(string text, int max)
    {
        if (text.Length <= max) return text;

        var cut = text[..max];

        // Only back off to a word boundary when the cut actually splits a word. If the budget
        // happens to end exactly on one, dropping the last whole word would waste it.
        var splitsAWord = !char.IsWhiteSpace(text[max]) && !char.IsWhiteSpace(cut[^1]);
        if (splitsAWord)
        {
            var lastSpace = cut.LastIndexOf(' ');
            // And only if the boundary does not throw away most of the budget.
            if (lastSpace >= max * 2 / 3) cut = cut[..lastSpace];
        }

        return cut.TrimEnd(' ', ',', ';', ':', '.', '-') + "…";
    }

    [RelayCommand]
    private async Task RetryRecordingAsync(RecoverableRecording? recording)
    {
        if (recording == null || IsListening || IsTranscribing || IsModelLoading) return;
        _transcriptionCts?.Dispose();
        _transcriptionCts = new CancellationTokenSource();
        PromptMode = recording.PromptMode;
        await StopListeningAndTranscribeAsync(recording);
    }

    [RelayCommand]
    private void DiscardRecording(RecoverableRecording? recording)
    {
        if (recording == null || IsListening || IsTranscribing) return;
        try
        {
            _recoveryStore.Delete(recording.Id);
            RecoverableRecordings.Remove(recording);
        }
        catch (Exception ex) { Log.Error("Could not discard recording", ex); ShowWarning("Could not remove the saved recording."); }
    }

    /// <summary>
    /// Trims leading and trailing silence from audio samples.
    /// Uses RMS energy in 100ms windows with a 200ms safety margin on each end.
    /// Reduces audio Whisper must process — typically 10-25% faster inference.
    /// </summary>
    private static float[] TrimSilence(float[] samples, float threshold = Constants.SilenceThreshold)
    {
        const int sampleRate = Constants.SampleRate;
        const int windowSize = Constants.SilenceWindowSamples;
        const int marginSamples = Constants.SilenceMarginSamples;

        if (samples.Length < windowSize * 3)
            return samples; // Too short to trim meaningfully

        // Find first non-silent window from start
        int start = 0;
        for (int i = 0; i <= samples.Length - windowSize; i += windowSize)
        {
            float sumSquares = 0;
            for (int j = 0; j < windowSize; j++)
                sumSquares += samples[i + j] * samples[i + j];
            float rms = MathF.Sqrt(sumSquares / windowSize);

            if (rms > threshold)
            {
                start = Math.Max(0, i - marginSamples);
                break;
            }
        }

        // Find last non-silent window from end
        int end = samples.Length;
        for (int i = samples.Length - windowSize; i >= 0; i -= windowSize)
        {
            float sumSquares = 0;
            for (int j = 0; j < windowSize; j++)
                sumSquares += samples[i + j] * samples[i + j];
            float rms = MathF.Sqrt(sumSquares / windowSize);

            if (rms > threshold)
            {
                end = Math.Min(samples.Length, i + windowSize + marginSamples);
                break;
            }
        }

        if (start >= end || (start == 0 && end == samples.Length))
            return samples; // Nothing to trim

        var trimmed = samples[start..end];
        var trimmedDuration = trimmed.Length / (float)sampleRate;
        var originalDuration = samples.Length / (float)sampleRate;
        Log.Info($"Silence trimmed: {originalDuration:F1}s → {trimmedDuration:F1}s (removed {originalDuration - trimmedDuration:F1}s)");
        return trimmed;
    }

    private void OnAudioLevelChanged(object? sender, float level)
    {
        if (_disposed || !IsListening) return;
        Volatile.Write(ref _latestAudioLevel, float.IsFinite(level) ? Math.Clamp(level, 0, 1) : 0);
        // A busy UI needs the newest meter reading, not a backlog of audio callbacks.
        if (Interlocked.Exchange(ref _audioUpdatePending, 1) != 0) return;
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _audioUpdatePending, 0);
            if (!_disposed) AudioLevel = IsListening ? Volatile.Read(ref _latestAudioLevel) : 0;
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    partial void OnIsListeningChanged(bool value)
    {
        Volatile.Write(ref _latestAudioLevel, 0);
        AudioLevel = 0;
    }

    [RelayCommand]
    public void OpenSettings()
    {
        Log.Info("OpenSettings command");
        if (IsListening || IsTranscribing)
        {
            ShowWarning("Finish or cancel the current recording before opening Settings.");
            return;
        }
        RequestShowSettings?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void DownloadUpdate()
    {
        if (string.IsNullOrEmpty(UpdateDownloadUrl))
        {
            Log.Warning("No update download URL available");
            return;
        }

        try
        {
            Log.Info($"Opening update download URL: {UpdateDownloadUrl}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = UpdateDownloadUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error("Failed to open update download URL", ex);
        }
    }

    [RelayCommand]
    public void DismissUpdate()
    {
        UpdateAvailable = false;
        Log.Info("Update notification dismissed");
    }

    [RelayCommand]
    public void CopyHistoryItem(TranscriptionHistoryItem? item)
    {
        if (item != null)
        {
            Log.Debug($"Copying history item: {item.Preview}");
            var copied = _clipboardService.SetText(item.Text);
            StatusText = copied ? "Copied to clipboard" : "Could not copy";
            if (!copied) ShowWarning("Could not copy the text. It is still available in History.");
            else RequestShowToast?.Invoke(this, new ToastEventArgs
            {
                Message = item.IsPrompt ? "Copied prompt to clipboard" : "Copied to clipboard",
                Type = ToastType.Success,
                DurationMs = 2000
            });
        }
    }

    [RelayCommand]
    public void CopyHistoryTranscription(TranscriptionHistoryItem? item)
    {
        if (item != null)
        {
            Log.Debug($"Copying history transcription: {item.TranscriptionPreview}");
            var copied = _clipboardService.SetText(item.Transcription);
            StatusText = copied ? "Copied transcription" : "Could not copy";
            if (!copied) ShowWarning("Could not copy the text. It is still available in History.");
            else RequestShowToast?.Invoke(this, new ToastEventArgs
            {
                Message = "Copied transcription",
                Type = ToastType.Success,
                DurationMs = 2000
            });
        }
    }

    [RelayCommand]
    public void DeleteHistoryItem(TranscriptionHistoryItem? item)
    {
        if (item == null) return;
        History.Remove(item);
        QueueHistorySave();
        Log.Info("History entry deleted");
    }

    [RelayCommand]
    public void ClearHistory()
    {
        if (History.Count == 0) return;
        var count = History.Count;
        History.Clear();
        QueueHistorySave();
        Log.Info($"History cleared ({count} entries)");
        StatusText = "History cleared";
    }

    public void ApplySettings(AppSettings settings)
    {
        Log.Info($"ApplySettings: Profile={settings.ModelProfile}, Mic={settings.SelectedMicrophoneId}, UseGpu={settings.UseGpu}, Hotkey={settings.HotkeyModifier}+{settings.HotkeyKey}");

        // Capture current settings to detect changes
        var previousProfile = _settingsService.Settings.ModelProfile;
        var previousUseGpu = _settingsService.Settings.UseGpu;

        // Update settings
        _settingsService.Settings.ModelProfile = settings.ModelProfile;
        _settingsService.Settings.CloudFallbackModel = settings.CloudFallbackModel;
        _transcriptionService.SetCloudFallback(settings.CloudFallbackModel);
        _settingsService.Settings.SelectedMicrophoneId = settings.SelectedMicrophoneId;
        _settingsService.Settings.CopyToClipboard = settings.CopyToClipboard;
        _settingsService.Settings.AutoPaste = settings.AutoPaste;
        _settingsService.Settings.RestoreClipboardAfterPaste = settings.RestoreClipboardAfterPaste;
        _settingsService.Settings.OverlayNearTextCursor = settings.OverlayNearTextCursor;
        _settingsService.Settings.Language = settings.Language;
        _settingsService.Settings.AutoDetectLanguage = settings.AutoDetectLanguage;
        _settingsService.Settings.UseGpu = settings.UseGpu;
        _settingsService.Settings.DuckVolumeWhileRecording = settings.DuckVolumeWhileRecording;
        _settingsService.Settings.VolumeDuckLevel = settings.VolumeDuckLevel;
        _settingsService.Settings.HotkeyModifier = settings.HotkeyModifier;

        // Apply volume duck level to the service
        if (_volumeDuckingService != null)
        {
            _volumeDuckingService.DuckLevel = settings.VolumeDuckLevel;
        }
        _settingsService.Settings.HotkeyKey = settings.HotkeyKey;

        // Save vocabulary settings
        _settingsService.Settings.UseCustomVocabulary = settings.UseCustomVocabulary;
        _settingsService.Settings.CustomVocabulary = settings.CustomVocabulary;
        _settingsService.Settings.TextReplacements = settings.TextReplacements;

        // Cloud API key (already encrypted by the settings dialog before it reaches here)
        _settingsService.Settings.OpenRouterApiKeyEncrypted = settings.OpenRouterApiKeyEncrypted;

        // Prompting model — persist the user's pick. Without this it was applied to the live refiner
        // (SetModel below) but never written to disk, so it reset to the default on every restart.
        _settingsService.Settings.PromptingModel = settings.PromptingModel;

        // Same lesson: a new AppSettings field that is not copied here is applied to the live
        // services but never written to disk, so it resets on the next restart.
        _settingsService.Settings.PromptFidelity = settings.PromptFidelity;
        _settingsService.Settings.PromptPlanning = settings.PromptPlanning;
        _settingsService.Settings.CommandMode = settings.CommandMode;
        _settingsService.Settings.CommandHotkeyModifier = settings.CommandHotkeyModifier;
        _settingsService.Settings.CommandHotkeyKey = settings.CommandHotkeyKey;
        _settingsService.Settings.CommandEndpoint = settings.CommandEndpoint;
        _settingsService.Settings.CommandToken = settings.CommandToken;

        _settingsService.Settings.UnloadModelWhenIdle = settings.UnloadModelWhenIdle;
        _transcriptionService.SetIdleUnload(settings.UnloadModelWhenIdle);

        // Keep the engine's language hint in sync so idle-unload reloads build the
        // processor with the right language directly.
        _transcriptionService.SetLanguageHint(settings.AutoDetectLanguage ? "auto" : settings.Language);

        // Refresh the reload hint too; the live Whisper processor detects and applies
        // vocabulary changes on the next recording, without changing the selected model.
        _transcriptionService.SetVocabularyPrompt(VocabularyPromptBuilder.Build(settings));

        _settingsService.Save();

        _audioCaptureService.SelectDevice(settings.SelectedMicrophoneId);

        // Re-forward the (decrypted) cloud key so the live cloud engine + prompt refiner pick up changes.
        var updatedKey = ApiKeyProtector.Unprotect(settings.OpenRouterApiKeyEncrypted);
        _transcriptionService.SetCloudApiKey(updatedKey);
        _promptRefinementService?.SetApiKey(updatedKey);
        _promptRefinementService?.SetModel(settings.PromptingModel);
        if (_promptFidelityService != null)
        {
            _promptFidelityService.SetApiKey(updatedKey);
            _promptFidelityService.Mode = settings.PromptFidelity;
        }
        _promptClassifier?.SetApiKey(updatedKey);

        // Reload model if profile or GPU setting changed
        bool profileChanged = previousProfile != settings.ModelProfile;
        bool gpuChanged = previousUseGpu != settings.UseGpu;

        if (profileChanged || gpuChanged)
        {
            Log.Info($"Model reload required: ProfileChanged={profileChanged}, GpuChanged={gpuChanged}, UseGpu={settings.UseGpu}");
            _ = LoadModelAsync(settings.ModelProfile, settings.UseGpu);
        }
        else
        {
            // Update display name even if model didn't change
            ModelProfileDisplay = settings.ModelProfile.GetDisplayName();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            Log.Info("MainViewModel disposing...");

            _audioCaptureService.AudioLevelChanged -= OnAudioLevelChanged;

            if (_audioCaptureService is IDisposable audioDisposable)
            {
                Log.Debug("Disposing AudioCaptureService");
                audioDisposable.Dispose();
            }

            if (_transcriptionService is IDisposable transcriptionDisposable)
            {
                Log.Debug("Disposing TranscriptionService");
                transcriptionDisposable.Dispose();
            }

            if (_volumeDuckingService is IDisposable volumeDisposable)
            {
                Log.Debug("Disposing VolumeDuckingService");
                volumeDisposable.Dispose();
            }

            _transcriptionCts?.Dispose();

            // Workers only use immutable snapshots, so shutdown can finish disk writes
            // without waiting on the dispatcher or losing the last history action.
            _historySaveTask.GetAwaiter().GetResult();

            Log.Info("MainViewModel disposed");
        }

        _disposed = true;
    }
}

public class TranscriptionHistoryItem
{
    private string? _displayText;
    private string? _displayTranscription;

    /// <summary>Final output that was copied/pasted: the generated prompt when Prompting ran, else the transcription.</summary>
    public string Text { get; init; } = "";

    /// <summary>The original spoken transcription — set ONLY when Prompting expanded it into a prompt, so an
    /// entry carries both "what I said" and "the prompt the app generated". Null for plain transcriptions.</summary>
    public string? RawTranscription { get; init; }

    public DateTime Timestamp { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>True when this entry was expanded by Prompting (it has both a transcription and a prompt).</summary>
    public bool IsPrompt => !string.IsNullOrWhiteSpace(RawTranscription);

    /// <summary>What the user actually said. For a prompted entry it's the raw field; otherwise Text already is it.</summary>
    public string Transcription => IsPrompt ? RawTranscription! : Text;

    // Let the available width control ellipsis. Fixed character limits waste space
    // in a resized window; line breaks should not expand a compact history card.
    public string DisplayText => _displayText ??= Text.ReplaceLineEndings(" ");
    public string DisplayTranscription => _displayTranscription ??= Transcription.ReplaceLineEndings(" ");

    public string Preview => Truncate(Text, 60);                       // final-output preview (prompt when prompted)
    public string TranscriptionPreview => Truncate(Transcription, 100); // "what I said"
    public string PromptPreview => Truncate(Text, 110);                // the generated prompt
    public string TimeDisplay => Timestamp.ToString("HH:mm:ss");

    private static string Truncate(string s, int max) => s.Length > max ? s[..(max - 3)] + "..." : s;
}
