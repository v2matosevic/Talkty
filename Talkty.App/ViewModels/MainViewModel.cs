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

    private readonly ISettingsService _settingsService;
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly ITranscriptionService _transcriptionService;
    private readonly IClipboardService _clipboardService;
    private readonly IUpdateService _updateService;
    private readonly IVolumeDuckingService? _volumeDuckingService;
    private readonly IAutoPasteService _autoPasteService;
    private readonly IPromptRefinementService? _promptRefinementService;

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
        IPromptRefinementService? promptRefinementService = null)
    {
        Log.Info("MainViewModel constructor starting");

        _settingsService = settingsService;
        _audioCaptureService = audioCaptureService;
        _transcriptionService = transcriptionService;
        _clipboardService = clipboardService;
        _updateService = updateService ?? new UpdateService();
        _volumeDuckingService = volumeDuckingService;
        _autoPasteService = autoPasteService ?? throw new ArgumentNullException(nameof(autoPasteService));
        _promptRefinementService = promptRefinementService;

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
            if (settings.UseCustomVocabulary && !settings.AutoDetectLanguage && settings.Language == "en")
            {
                _transcriptionService.SetVocabularyPrompt(DefaultVocabulary.PromptContext);
            }

            // Forward the decrypted cloud API key to the transcription engine AND the prompt
            // refiner — both call OpenRouter.
            var cloudKey = ApiKeyProtector.Unprotect(settings.OpenRouterApiKeyEncrypted);
            _transcriptionService.SetCloudApiKey(cloudKey);
            _promptRefinementService?.SetApiKey(cloudKey);
            _promptRefinementService?.SetModel(settings.PromptingModel);

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

    private void SaveHistoryToDisk()
    {
        try
        {
            var entries = History.Select(h => new TranscriptionHistoryEntry
            {
                Text = h.Text,
                RawTranscription = h.RawTranscription,
                Timestamp = h.Timestamp,
                DurationSeconds = h.Duration.TotalSeconds
            }).ToList();

            _settingsService.SaveHistory(entries);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save history to disk", ex);
        }
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

        if (!IsModelLoaded)
        {
            Log.Warning("Model not loaded - cannot start listening");
            StatusText = "Model not loaded";
            return;
        }

        if (IsListening)
        {
            Log.Info("Stopping listening and starting transcription");
            _ = StopListeningAndTranscribeAsync();
        }
        else
        {
            Log.Info("Starting listening");
            _ = StartListeningAsync();
        }
    }

    /// <summary>
    /// Cancels the current recording without transcribing.
    /// Called when user presses ESC during recording.
    /// </summary>
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

    private async Task StartListeningAsync()
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

            Log.Debug("Calling AudioCaptureService.StartRecording()");
            _audioCaptureService.StartRecording();

            // If the model was unloaded while idle, start reloading NOW so it overlaps with
            // the user speaking — by stop time it's usually ready and the reload costs no
            // perceived latency. TranscribeAsync awaits this same load if it's still going.
            _ = _transcriptionService.EnsureModelLoadedAsync();

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

    private async Task StopListeningAndTranscribeAsync()
    {
        try
        {
            // Capture the foreground window NOW (at stop time) — this is the app
            // the user is currently looking at and wants to paste into.
            _autoPasteService.CaptureTargetWindow();

            // Claim foreground privilege IMMEDIATELY on the UI thread.
            // Windows only grants SetForegroundWindow permission to the thread
            // that last received user input (our hotkey). If we wait until after
            // transcription (~1s later), the privilege expires and paste fails.
            _autoPasteService.ClaimForegroundPrivilege();

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
            await _audioCaptureService.StopRecordingAndFlushAsync(Constants.RecordingFlushTimeoutMs);

            Log.Debug("Getting recorded audio samples");
            var audioSamples = _audioCaptureService.GetRecordedAudioAsFloat();
            Log.Info($"Audio samples: {audioSamples.Length} ({audioSamples.Length / (float)Constants.SampleRate:F1}s at 16kHz)");

            if (audioSamples.Length == 0)
            {
                Log.Warning("No audio recorded!");
                StatusText = "No audio recorded";
                IsTranscribing = false;
                RequestHideOverlay?.Invoke(this, EventArgs.Empty);
                return;
            }

            // Trim leading/trailing silence — reduces audio Whisper must process (10-25% faster)
            audioSamples = TrimSilence(audioSamples);

            Log.Info("Starting transcription...");
            var startTime = DateTime.Now;
            var language = _settingsService.Settings.AutoDetectLanguage ? "auto" : _settingsService.Settings.Language;

            // Build vocabulary prompt — use contextual sentences for stronger Whisper bias.
            // English-only: an English initial_prompt biases non-English (and auto-detect)
            // decoding toward English tokens and prompt regurgitation.
            string? vocabularyPrompt = null;
            if (_settingsService.Settings.UseCustomVocabulary && language == "en")
            {
                vocabularyPrompt = DefaultVocabulary.PromptContext;
                Log.Debug($"Vocabulary prompt: {vocabularyPrompt.Length} chars");
            }

            // Load text replacements for post-processing (applied after Whisper output)
            var textReplacements = _settingsService.Settings.UseCustomVocabulary
                ? _settingsService.Settings.TextReplacements
                : null;

            // Streaming callback: copy first segment to clipboard immediately (before full transcription completes).
            // This lets clipboard-only users paste sooner. Auto-paste waits for full text.
            Action<string>? onFirstSegment = null;
            if (_settingsService.Settings.CopyToClipboard)
            {
                onFirstSegment = (text) =>
                {
                    try
                    {
                        // Strip hallucinations first — otherwise "Thanks for watching" can
                        // hit the clipboard before the full-pass clean runs.
                        text = TextPostProcessor.StripHallucinations(text);

                        // Apply post-processing to streamed segment too
                        if (textReplacements is { Count: > 0 })
                            text = TextPostProcessor.ApplyReplacements(text, textReplacements);

                        if (string.IsNullOrWhiteSpace(text))
                        {
                            Log.Debug("First segment empty after cleanup — skipping clipboard");
                            return;
                        }

                        Application.Current.Dispatcher.Invoke(() =>
                        {
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

            var cancellationToken = _transcriptionCts?.Token ?? default;
            var result = await _transcriptionService.TranscribeAsync(audioSamples, language, cancellationToken, onFirstSegment, vocabularyPrompt);
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

                Log.Info($"Transcribed text ({result.Text.Length} chars): \"{result.Text}\"");

                // Prompt mode: expand the transcription into a structured coding-agent prompt
                // before it hits the clipboard/paste. Falls back to the raw text on any failure.
                // When a prompt IS generated, keep the original transcription so history shows both.
                string? rawTranscriptionForHistory = null;
                if (PromptMode)
                {
                    if (_promptRefinementService?.IsConfigured == true)
                    {
                        StatusText = "Refining prompt...";
                        var rawTranscription = result.Text;
                        var refined = await _promptRefinementService.RefineAsync(result.Text, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(refined))
                        {
                            Log.Info($"Prompt mode: expanded into agent prompt ({result.Text.Length} → {refined.Length} chars)");
                            result.Text = refined;
                            rawTranscriptionForHistory = rawTranscription;
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

                if (_settingsService.Settings.CopyToClipboard)
                {
                    // Update clipboard with full text (overwrites first-segment partial if multi-segment)
                    Log.Debug("Copying full text to clipboard");
                    bool clipboardSuccess = false;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        clipboardSuccess = _clipboardService.SetText(result.Text);
                        Log.Debug($"Clipboard copy result: {clipboardSuccess}");
                    });

                    if (_settingsService.Settings.AutoPaste && clipboardSuccess)
                    {
                        // Run paste on thread pool so Thread.Sleep calls don't block UI thread.
                        // Overlay stays visible during paste — hiding it would cause focus changes.
                        Log.Debug("Auto-pasting at cursor");
                        var textForClipboard = result.Text;
                        await Task.Run(() => _autoPasteService.PasteToTargetWindow(
                            ensureClipboardText: () =>
                            {
                                // Re-set clipboard right before Ctrl+V — focus switching can
                                // cause some apps to clear or claim the clipboard.
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    _clipboardService.SetText(textForClipboard);
                                });
                            }));
                    }
                }

                StatusText = "Copied to clipboard";

                // History update + disk persist — fire-and-forget, don't block status reset
                Application.Current.Dispatcher.Invoke(() =>
                {
                    History.Insert(0, new TranscriptionHistoryItem
                    {
                        Text = result.Text,
                        RawTranscription = rawTranscriptionForHistory,
                        Timestamp = result.Timestamp,
                        Duration = result.Duration
                    });

                    if (History.Count > Constants.MaxHistoryEntries)
                    {
                        History.RemoveAt(History.Count - 1);
                    }
                });
                _ = Task.Run(SaveHistoryToDisk);
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
                    StatusText = result.ErrorMessage ?? "Transcription failed";
                    // The app usually lives hidden in the tray — StatusText alone is invisible
                    // there. Toast so the user knows nothing reached the clipboard.
                    RequestShowToast?.Invoke(this, new ToastEventArgs
                    {
                        Message = result.ErrorMessage ?? "Transcription failed — nothing was copied",
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
                StatusText = "Ready";
            }
        }
        catch (Exception ex)
        {
            Log.Error("StopListeningAndTranscribe failed", ex);
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
        // InvokeAsync (fire-and-forget) so the NAudio callback thread never blocks on UI
        Application.Current.Dispatcher.InvokeAsync(() => AudioLevel = level);
    }

    [RelayCommand]
    public void OpenSettings()
    {
        Log.Info("OpenSettings command");
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
            _clipboardService.SetText(item.Text);
            StatusText = "Copied to clipboard";
        }
    }

    [RelayCommand]
    public void CopyHistoryTranscription(TranscriptionHistoryItem? item)
    {
        if (item != null)
        {
            Log.Debug($"Copying history transcription: {item.TranscriptionPreview}");
            _clipboardService.SetText(item.Transcription);
            StatusText = "Copied transcription";
        }
    }

    public void ApplySettings(AppSettings settings)
    {
        Log.Info($"ApplySettings: Profile={settings.ModelProfile}, Mic={settings.SelectedMicrophoneId}, UseGpu={settings.UseGpu}, Hotkey={settings.HotkeyModifier}+{settings.HotkeyKey}");

        // Capture current settings to detect changes
        var previousProfile = _settingsService.Settings.ModelProfile;
        var previousUseGpu = _settingsService.Settings.UseGpu;

        // Update settings
        _settingsService.Settings.ModelProfile = settings.ModelProfile;
        _settingsService.Settings.SelectedMicrophoneId = settings.SelectedMicrophoneId;
        _settingsService.Settings.CopyToClipboard = settings.CopyToClipboard;
        _settingsService.Settings.AutoPaste = settings.AutoPaste;
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

        _settingsService.Settings.UnloadModelWhenIdle = settings.UnloadModelWhenIdle;
        _transcriptionService.SetIdleUnload(settings.UnloadModelWhenIdle);

        _settingsService.Save();

        _audioCaptureService.SelectDevice(settings.SelectedMicrophoneId);

        // Re-forward the (decrypted) cloud key so the live cloud engine + prompt refiner pick up changes.
        var updatedKey = ApiKeyProtector.Unprotect(settings.OpenRouterApiKeyEncrypted);
        _transcriptionService.SetCloudApiKey(updatedKey);
        _promptRefinementService?.SetApiKey(updatedKey);
        _promptRefinementService?.SetModel(settings.PromptingModel);

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

            Log.Info("MainViewModel disposed");
        }

        _disposed = true;
    }
}

public class TranscriptionHistoryItem
{
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

    public string Preview => Truncate(Text, 60);                       // final-output preview (prompt when prompted)
    public string TranscriptionPreview => Truncate(Transcription, 100); // "what I said"
    public string PromptPreview => Truncate(Text, 110);                // the generated prompt
    public string TimeDisplay => Timestamp.ToString("HH:mm:ss");

    private static string Truncate(string s, int max) => s.Length > max ? s[..(max - 3)] + "..." : s;
}
