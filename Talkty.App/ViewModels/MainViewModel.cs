using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Talkty.App.Models;
using Talkty.App.Services;

namespace Talkty.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private bool _disposed;

    // SendInput API for reliable keyboard simulation
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int ASFW_ANY = -1;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const ushort VK_MENU = 0x12;  // Alt key
    private const ushort VK_SHIFT = 0x10;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    // INPUT structure for SendInput - must be properly sized for 64-bit
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;

        public static int Size => Marshal.SizeOf(typeof(INPUT));
    }

    // Union - use explicit layout with proper padding for 64-bit
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    private readonly ISettingsService _settingsService;
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly ITranscriptionService _transcriptionService;
    private readonly IClipboardService _clipboardService;
    private readonly IUpdateService _updateService;
    private readonly IVolumeDuckingService? _volumeDuckingService;

    // Store the foreground window when recording starts for auto-paste
    private IntPtr _targetWindowHandle = IntPtr.Zero;

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
        IVolumeDuckingService? volumeDuckingService = null)
    {
        Log.Info("MainViewModel constructor starting");

        _settingsService = settingsService;
        _audioCaptureService = audioCaptureService;
        _transcriptionService = transcriptionService;
        _clipboardService = clipboardService;
        _updateService = updateService ?? new UpdateService();
        _volumeDuckingService = volumeDuckingService;

        _audioCaptureService.AudioLevelChanged += OnAudioLevelChanged;
        Log.Debug("AudioLevelChanged event handler attached");

        LoadSettingsAndModel();
        Log.Info("MainViewModel constructor completed");
    }

    private async void LoadSettingsAndModel()
    {
        Log.Info("LoadSettingsAndModel starting");

        _settingsService.Load();
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

        await LoadModelAsync(settings.ModelProfile, settings.UseGpu);

        // Check for updates in background (non-blocking)
        _ = CheckForUpdatesAsync();
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
            foreach (var entry in entries.Take(50)) // Limit to 50 entries
            {
                History.Add(new TranscriptionHistoryItem
                {
                    Text = entry.Text,
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
                var backendType = BackendInfo.Contains("CUDA", StringComparison.OrdinalIgnoreCase) ? "GPU" : "CPU";
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
            StopListeningAndTranscribe();
        }
        else
        {
            Log.Info("Starting listening");
            StartListening();
        }
    }

    /// <summary>
    /// Cancels the current recording without transcribing.
    /// Called when user presses ESC during recording.
    /// </summary>
    public void CancelRecording()
    {
        if (!IsListening)
        {
            Log.Debug("CancelRecording called but not listening");
            return;
        }

        Log.Info(">>> RECORDING CANCELLED (ESC) <<<");

        try
        {
            // Stop recording without getting audio
            _audioCaptureService.StopRecording();
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
            Task.Delay(1000).ContinueWith(_ =>
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

    private async void StartListening()
    {
        bool volumeDucked = false;
        try
        {
            // Capture the foreground window BEFORE showing overlay (for auto-paste later)
            _targetWindowHandle = GetForegroundWindow();
            Log.Debug($"Target window for auto-paste: {_targetWindowHandle}");

            // Duck system volume if enabled
            if (_settingsService.Settings.DuckVolumeWhileRecording && _volumeDuckingService != null)
            {
                Log.Debug("Ducking volume before recording");
                await _volumeDuckingService.DuckAsync();
                volumeDucked = true;
            }

            Log.Debug("Calling AudioCaptureService.StartRecording()");
            _audioCaptureService.StartRecording();
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

    private async void StopListeningAndTranscribe()
    {
        try
        {
            Log.Debug("Calling AudioCaptureService.StopRecording()");
            _audioCaptureService.StopRecording();

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

            Log.Debug("Getting recorded audio samples");
            var audioSamples = _audioCaptureService.GetRecordedAudioAsFloat();
            Log.Info($"Audio samples: {audioSamples.Length} ({audioSamples.Length / 16000.0:F1}s at 16kHz)");

            if (audioSamples.Length == 0)
            {
                Log.Warning("No audio recorded!");
                StatusText = "No audio recorded";
                IsTranscribing = false;
                RequestHideOverlay?.Invoke(this, EventArgs.Empty);
                return;
            }

            Log.Info("Starting transcription...");
            var startTime = DateTime.Now;
            var language = _settingsService.Settings.AutoDetectLanguage ? "auto" : _settingsService.Settings.Language;
            var result = await _transcriptionService.TranscribeAsync(audioSamples, language);
            var elapsed = DateTime.Now - startTime;

            Log.Info($"Transcription completed in {elapsed.TotalSeconds:F1}s. Success: {result.Success}");

            if (result.Success && !string.IsNullOrWhiteSpace(result.Text))
            {
                Log.Info($"Transcribed text ({result.Text.Length} chars): \"{result.Text}\"");

                if (_settingsService.Settings.CopyToClipboard)
                {
                    Log.Debug("Copying to clipboard");
                    bool clipboardSuccess = false;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        clipboardSuccess = _clipboardService.SetText(result.Text);
                        Log.Debug($"Clipboard copy result: {clipboardSuccess}");
                    });

                    if (_settingsService.Settings.AutoPaste && clipboardSuccess)
                    {
                        // Wait longer for larger models - transcription can take a while
                        // and we need to ensure the overlay has time to update
                        var pasteDelay = _settingsService.Settings.ModelProfile switch
                        {
                            ModelProfile.Large => 300,
                            ModelProfile.Medium => 250,
                            ModelProfile.Small => 200,
                            _ => 150
                        };
                        Log.Debug($"Auto-pasting at cursor after {pasteDelay}ms delay (model: {_settingsService.Settings.ModelProfile})");
                        await Task.Delay(pasteDelay);
                        SimulatePaste();
                    }
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    History.Insert(0, new TranscriptionHistoryItem
                    {
                        Text = result.Text,
                        Timestamp = result.Timestamp,
                        Duration = result.Duration
                    });

                    if (History.Count > 50)
                    {
                        History.RemoveAt(History.Count - 1);
                    }

                    // Persist to disk
                    SaveHistoryToDisk();
                });

                StatusText = "Copied to clipboard";
            }
            else
            {
                Log.Warning($"Transcription failed or empty. Error: {result.ErrorMessage}");
                StatusText = result.ErrorMessage ?? "Transcription failed";
            }

            IsTranscribing = false;

            await Task.Delay(1000);
            Log.Debug("Raising RequestHideOverlay");
            RequestHideOverlay?.Invoke(this, EventArgs.Empty);

            await Task.Delay(500);
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

    private void OnAudioLevelChanged(object? sender, float level)
    {
        Application.Current.Dispatcher.Invoke(() => AudioLevel = level);
    }

    private void SimulatePaste()
    {
        // Robust auto-paste approach:
        // 1. Wait for modifier keys to be released (user just pressed Alt+Q)
        // 2. Restore focus to the target window we captured when recording started
        // 3. Verify clipboard contains our text
        // 4. Small delay for window activation to complete
        // 5. Send Ctrl+V with retry

        try
        {
            Log.Debug($"SimulatePaste starting. Target window: {_targetWindowHandle}");

            // Validate target window
            if (_targetWindowHandle == IntPtr.Zero || !IsWindow(_targetWindowHandle))
            {
                Log.Warning("Target window handle is invalid");
                return;
            }

            // Wait for modifier keys (especially Alt from Alt+Q hotkey) to be released
            WaitForModifierKeysRelease();

            // Verify clipboard is ready
            if (!VerifyClipboardReady())
            {
                Log.Warning("Clipboard not ready after waiting");
            }

            // Restore focus to the original target window
            if (!RestoreFocusToTargetWindow())
            {
                Log.Warning("Failed to restore focus, attempting paste anyway");
            }

            // Small delay for window activation to complete
            Thread.Sleep(100);

            // Verify focus one more time
            var currentForeground = GetForegroundWindow();
            if (currentForeground != _targetWindowHandle)
            {
                Log.Warning($"Focus mismatch before paste. Current: {currentForeground}, Target: {_targetWindowHandle}");
                // Try one more time
                SetForegroundWindow(_targetWindowHandle);
                Thread.Sleep(50);
            }

            // Send Ctrl+V
            SendCtrlV();

            Log.Info("Auto-paste completed");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to simulate paste", ex);
        }
    }

    private bool VerifyClipboardReady()
    {
        // Give clipboard a moment to stabilize and verify it has content
        for (int i = 0; i < 5; i++)
        {
            try
            {
                bool hasText = false;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    hasText = System.Windows.Clipboard.ContainsText();
                });

                if (hasText)
                {
                    Log.Debug($"Clipboard verified ready on attempt {i + 1}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"Clipboard check attempt {i + 1} failed: {ex.Message}");
            }
            Thread.Sleep(20);
        }
        return false;
    }

    private bool RestoreFocusToTargetWindow()
    {
        try
        {
            Log.Debug($"Restoring focus to window: {_targetWindowHandle}");

            // Check if window is minimized and restore it
            if (IsIconic(_targetWindowHandle))
            {
                Log.Debug("Window is minimized, restoring...");
                ShowWindow(_targetWindowHandle, SW_RESTORE);
                Thread.Sleep(50);
            }

            // Get our thread and target thread IDs for attaching input
            uint currentThreadId = GetCurrentThreadId();
            uint targetThreadId = GetWindowThreadProcessId(_targetWindowHandle, out _);

            Log.Debug($"Current thread: {currentThreadId}, Target thread: {targetThreadId}");

            bool attached = false;
            if (currentThreadId != targetThreadId)
            {
                attached = AttachThreadInput(currentThreadId, targetThreadId, true);
                Log.Debug($"AttachThreadInput result: {attached}");
            }

            try
            {
                // Allow any process to set foreground window
                AllowSetForegroundWindow(ASFW_ANY);

                // Bring window to top
                BringWindowToTop(_targetWindowHandle);

                // Set foreground window
                var result = SetForegroundWindow(_targetWindowHandle);
                Log.Debug($"SetForegroundWindow result: {result}");

                // Check what window is actually in foreground now
                var currentForeground = GetForegroundWindow();
                var success = currentForeground == _targetWindowHandle;
                Log.Debug($"Current foreground after restore: {currentForeground}, Target: {_targetWindowHandle}, Match: {success}");
                return success;
            }
            finally
            {
                // Detach thread input
                if (attached)
                {
                    AttachThreadInput(currentThreadId, targetThreadId, false);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to restore focus to target window", ex);
            return false;
        }
    }

    private void WaitForModifierKeysRelease()
    {
        // Wait up to 500ms for Alt, Ctrl, Shift to be released
        // This is critical because the user just pressed Alt+Q to stop recording
        var timeout = DateTime.Now.AddMilliseconds(500);
        while (DateTime.Now < timeout)
        {
            bool altPressed = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            bool ctrlPressed = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            bool shiftPressed = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

            if (!altPressed && !ctrlPressed && !shiftPressed)
            {
                Log.Debug("All modifier keys released");
                return;
            }

            Thread.Sleep(10);
        }
        Log.Warning("Timeout waiting for modifier keys to release");
    }

    private void SendCtrlV()
    {
        var inputs = new INPUT[4];

        // Get the size once and log it for debugging
        int inputSize = INPUT.Size;
        Log.Debug($"INPUT struct size: {inputSize} bytes");

        // Ctrl down
        inputs[0] = new INPUT { type = INPUT_KEYBOARD };
        inputs[0].u.ki.wVk = VK_CONTROL;
        inputs[0].u.ki.wScan = 0;
        inputs[0].u.ki.dwFlags = 0;
        inputs[0].u.ki.time = 0;
        inputs[0].u.ki.dwExtraInfo = IntPtr.Zero;

        // V down
        inputs[1] = new INPUT { type = INPUT_KEYBOARD };
        inputs[1].u.ki.wVk = VK_V;
        inputs[1].u.ki.wScan = 0;
        inputs[1].u.ki.dwFlags = 0;
        inputs[1].u.ki.time = 0;
        inputs[1].u.ki.dwExtraInfo = IntPtr.Zero;

        // V up
        inputs[2] = new INPUT { type = INPUT_KEYBOARD };
        inputs[2].u.ki.wVk = VK_V;
        inputs[2].u.ki.wScan = 0;
        inputs[2].u.ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[2].u.ki.time = 0;
        inputs[2].u.ki.dwExtraInfo = IntPtr.Zero;

        // Ctrl up
        inputs[3] = new INPUT { type = INPUT_KEYBOARD };
        inputs[3].u.ki.wVk = VK_CONTROL;
        inputs[3].u.ki.wScan = 0;
        inputs[3].u.ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[3].u.ki.time = 0;
        inputs[3].u.ki.dwExtraInfo = IntPtr.Zero;

        var result = SendInput((uint)inputs.Length, inputs, inputSize);

        if (result != inputs.Length)
        {
            var error = Marshal.GetLastWin32Error();
            Log.Error($"SendInput failed. Sent: {result}/{inputs.Length}, Error: {error}, Size: {inputSize}");
        }
        else
        {
            Log.Debug($"Ctrl+V sent successfully ({result} inputs)");
        }
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
        _settingsService.Settings.ShowNotification = settings.ShowNotification;
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
        _settingsService.Save();

        _audioCaptureService.SelectDevice(settings.SelectedMicrophoneId);

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

            Log.Info("MainViewModel disposed");
        }

        _disposed = true;
    }
}

public class TranscriptionHistoryItem
{
    public string Text { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public TimeSpan Duration { get; init; }

    public string Preview => Text.Length > 60 ? Text[..57] + "..." : Text;
    public string TimeDisplay => Timestamp.ToString("HH:mm:ss");
}

public enum ToastType
{
    Info,
    Success,
    Warning,
    Tip
}

public class ToastEventArgs : EventArgs
{
    public string Message { get; init; } = "";
    public ToastType Type { get; init; } = ToastType.Info;
    public int DurationMs { get; init; } = 3000;
}
