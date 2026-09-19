using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Talkty.App.Controls;
using Talkty.App.Models;
using Talkty.App.Services;
using Talkty.App.ViewModels;
using Talkty.App.Views;

namespace Talkty.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IHotkeyService _hotkeyService;
    private readonly ISettingsService _settingsService;
    private readonly IAudioCaptureService _audioCaptureService;
    private OverlayWindow? _overlayWindow;
    private System.Windows.Threading.DispatcherTimer? _commandLinger;
    private SettingsWindow? _settingsWindow;
    private bool _isExiting;
    private DateTime _lastHotkeyTime = DateTime.MinValue;
    private static readonly TimeSpan HotkeyDebounceInterval = TimeSpan.FromMilliseconds(Constants.HotkeyDebounceMs);
    private bool _hasShownTrayHint;

    public MainWindow()
    {
        Log.Info("MainWindow constructor starting");

        try
        {
            InitializeComponent();
            Log.Debug("InitializeComponent completed");
        }
        catch (Exception ex)
        {
            Log.Error("InitializeComponent FAILED", ex);
            throw;
        }

        // Initialize services
        Log.Info("Initializing services...");

        _settingsService = new SettingsService();
        _settingsService.Load();
        Log.Debug("SettingsService created");

        // Track app launch and hint state
        _settingsService.Settings.Hints.AppLaunchCount++;
        _hasShownTrayHint = _settingsService.Settings.Hints.HasSeenTrayMinimizeHint;

        _audioCaptureService = new AudioCaptureService();
        Log.Debug("AudioCaptureService created");

        var transcriptionService = new TranscriptionService();
        Log.Debug("TranscriptionService created");

        var clipboardService = new ClipboardService();
        Log.Debug("ClipboardService created");

        _hotkeyService = new HotkeyService();
        Log.Debug("HotkeyService created");

        var volumeDuckingService = new VolumeDuckingService();
        Log.Debug("VolumeDuckingService created");

        var autoPasteService = new AutoPasteService();
        Log.Debug("AutoPasteService created");

        var promptRefinementService = new PromptRefinementService();
        Log.Debug("PromptRefinementService created");

        // Optional fidelity check on a generated prompt. Reuses the same OpenRouter key; the mode
        // is applied from settings by the ViewModel.
        var promptFidelityService = new PromptFidelityService();
        Log.Debug("PromptFidelityService created");

        // Classifies a dictation before refinement. Inert until PromptPlanning is switched on.
        var promptClassifier = new PromptClassifier();
        Log.Debug("PromptClassifier created");

        var voiceCommandService = new VoiceCommandService(_settingsService);
        Log.Debug("VoiceCommandService created");

        // Initialize ViewModel
        Log.Info("Creating MainViewModel...");
        _viewModel = new MainViewModel(
            _settingsService,
            _audioCaptureService,
            transcriptionService,
            clipboardService,
            volumeDuckingService: volumeDuckingService,
            autoPasteService: autoPasteService,
            promptRefinementService: promptRefinementService,
            promptFidelityService: promptFidelityService,
            voiceCommandService: voiceCommandService,
            promptClassifier: promptClassifier);

        _viewModel.RequestShowOverlay += OnRequestShowOverlay;
        _viewModel.RequestHideOverlay += OnRequestHideOverlay;
        _viewModel.CommandProgress += OnCommandProgress;
        _viewModel.RequestShowSettings += OnRequestShowSettings;
        _viewModel.RequestShowToast += OnRequestShowToast;
        _viewModel.RecordingStarted += OnRecordingStarted;
        _viewModel.RecordingStopped += OnRecordingStopped;

        DataContext = _viewModel;
        Log.Debug("DataContext set");

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;

        Log.Info("MainWindow constructor completed");
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // Double-click toggles maximize (disabled since we use CanResizeWithGrip)
            return;
        }
        DragMove();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log.Info("MainWindow Loaded event");

        var handle = new WindowInteropHelper(this).Handle;
        Log.Debug($"Window handle: {handle}");

        // Register hotkey from settings
        RegisterConfiguredHotkey(handle);
        RegisterCommandHotkey(handle);

        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.CancelHotkeyPressed += OnCancelHotkeyPressed;
        _hotkeyService.CommandHotkeyPressed += OnCommandHotkeyPressed;
        Log.Debug("Hotkey event handlers attached");

        Log.Info($"MainWindow loaded. Size: {Width}x{Height}, Position: {Left},{Top}");

        // Update hotkey badge display
        UpdateHotkeyBadge();

        // Show onboarding on first run
        if (_settingsService.IsFirstRun)
        {
            Log.Info("First run detected, showing onboarding");
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ShowOnboarding);
        }
        else if (_settingsService.Settings.Hints.AppLaunchCount == 2)
        {
            // Second launch - show a quick tip
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                Toast.Show("Tip: Press the hotkey from any app to start recording", ToastType.Tip, 4000);
            });
        }
    }

    private void RegisterConfiguredHotkey(nint handle)
    {
        var settings = _settingsService.Settings;
        var modifier = settings.HotkeyModifier;
        var key = settings.HotkeyKey;

        Log.Info($"Registering global hotkey: {modifier} + {key}...");
        var registered = _hotkeyService.Register(handle, modifier, key);

        if (!registered)
        {
            Log.Error($"Failed to register hotkey {modifier} + {key}!");
            // In-app toast instead of the light OS MessageBox (which broke the dark theme).
            Toast.Show(
                $"Hotkey {modifier}+{key} is in use by another app — pick a different one in Settings",
                ToastType.Warning,
                6000);
        }
        else
        {
            Log.Info($"Hotkey {modifier} + {key} registered successfully");
        }
    }

    private void UpdateHotkeyBadge()
    {
        var settings = _settingsService.Settings;
        var hotkeyText = $"{settings.HotkeyModifier}+{settings.HotkeyKey}";
        HotkeyBadge.Text = hotkeyText;
        EmptyStateHotkeyText.Text = $"Press {hotkeyText} to start";
        Log.Debug($"Hotkey badge updated: {hotkeyText}");
    }

    private void ShowOnboarding()
    {
        try
        {
            var onboarding = new OnboardingWindow { Owner = this };
            onboarding.SetHotkey(
                _settingsService.Settings.HotkeyModifier,
                _settingsService.Settings.HotkeyKey);

            var result = onboarding.ShowDialog();

            if (result == true && onboarding.OpenSettingsRequested)
            {
                Log.Info("User requested settings from onboarding");
                _viewModel.OpenSettingsCommand.Execute(null);
            }

            // Save settings to mark first run as complete
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to show onboarding", ex);
        }
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        // Settings and dictation share a recorder. A modal microphone test must not
        // become the beginning of a real dictation or be stopped by the global hotkey.
        if (_settingsWindow?.IsVisible == true)
        {
            _settingsWindow.Activate();
            return;
        }
        var now = DateTime.Now;
        var elapsed = now - _lastHotkeyTime;

        if (elapsed < HotkeyDebounceInterval)
        {
            Log.Debug($"Hotkey debounced. Elapsed: {elapsed.TotalMilliseconds:F0}ms < {HotkeyDebounceInterval.TotalMilliseconds}ms");
            return;
        }

        _lastHotkeyTime = now;
        Log.Info(">>> HOTKEY PRESSED (Alt+Q) <<<");

        Dispatcher.Invoke(() =>
        {
            Log.Debug("Executing ToggleListeningCommand");
            _viewModel.ToggleListeningCommand.Execute(null);
        });
    }

    /// <summary>
    /// Command mode. Same debounce and same modal guard as dictation, because it is
    /// the same recorder; only the destination of the finished transcript differs.
    /// </summary>
    private void OnCommandHotkeyPressed(object? sender, EventArgs e)
    {
        if (_settingsWindow?.IsVisible == true)
        {
            _settingsWindow.Activate();
            return;
        }

        var now = DateTime.Now;
        if (now - _lastHotkeyTime < HotkeyDebounceInterval)
        {
            Log.Debug("Command hotkey debounced");
            return;
        }

        _lastHotkeyTime = now;
        var settings = _settingsService.Settings;
        Log.Info($">>> COMMAND HOTKEY PRESSED ({settings.CommandHotkeyModifier}+{settings.CommandHotkeyKey}) <<<");

        Dispatcher.Invoke(() => _viewModel.ToggleCommandListeningCommand.Execute(null));
    }

    /// <summary>
    /// Registered only when command mode is on, so the key stays free for other apps
    /// for everyone who does not use it.
    /// </summary>
    private void RegisterCommandHotkey(nint handle)
    {
        var settings = _settingsService.Settings;
        if (!settings.CommandMode)
        {
            _hotkeyService.UnregisterCommandHotkey();
            return;
        }

        var registered = _hotkeyService.RegisterCommandHotkey(
            handle, settings.CommandHotkeyModifier, settings.CommandHotkeyKey);

        if (!registered)
        {
            Toast.Show(
                $"Command hotkey {settings.CommandHotkeyModifier}+{settings.CommandHotkeyKey} is in use by another app",
                ToastType.Warning,
                6000);
        }
    }

    private void OnCancelHotkeyPressed(object? sender, EventArgs e)
    {
        Log.Info(">>> CANCEL HOTKEY PRESSED (ESC) <<<");
        Dispatcher.Invoke(() =>
        {
            _viewModel.CancelRecording();
        });
    }

    private void OnRecordingStarted(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            _hotkeyService.RegisterCancelHotkey(handle);
        });
    }

    private void OnRecordingStopped(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _hotkeyService.UnregisterCancelHotkey();
        });
    }

    private void OnRequestShowOverlay(object? sender, EventArgs e)
    {
        Log.Info("RequestShowOverlay received");
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (_overlayWindow == null || !_overlayWindow.IsLoaded)
                {
                    Log.Debug("Creating new OverlayWindow");
                    _overlayWindow = new OverlayWindow();
                }

                // Position mode can change in Settings — refresh it every show
                _overlayWindow.PositionNearTextCursor = _settingsService.Settings.OverlayNearTextCursor;

                // Reset overlay state for new recording session
                _commandLinger?.Stop();
                _overlayWindow.ViewModel.ClearCommand();
                _overlayWindow.ViewModel.IsListening = true;
                _overlayWindow.ViewModel.IsTranscribing = false;
                _overlayWindow.ViewModel.StatusText = "Listening...";
                _overlayWindow.ViewModel.StartTimer();

                // Reset & wire the "Prompting" toggle for this recording (detach first to avoid dupes)
                _overlayWindow.ViewModel.PropertyChanged -= OnOverlayPromptModeChanged;
                _overlayWindow.ViewModel.IsPromptMode = false;
                _viewModel.PromptMode = false;
                _overlayWindow.ViewModel.PropertyChanged += OnOverlayPromptModeChanged;

                _overlayWindow.Show();
                Log.Info("OverlayWindow shown");

                // Wire up audio level updates (detach first to prevent duplicate handlers)
                if (_viewModel is { } vm)
                {
                    vm.PropertyChanged -= OnViewModelPropertyChanged;
                    vm.PropertyChanged += OnViewModelPropertyChanged;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed to show overlay", ex);
            }
        });
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_overlayWindow?.ViewModel == null) return;

        switch (e.PropertyName)
        {
            case nameof(MainViewModel.AudioLevel):
                _overlayWindow.ViewModel.AudioLevel = _viewModel.AudioLevel;
                break;
            case nameof(MainViewModel.IsListening):
                Log.Debug($"IsListening changed to: {_viewModel.IsListening}");
                _overlayWindow.ViewModel.IsListening = _viewModel.IsListening;
                if (!_viewModel.IsListening) _overlayWindow.ViewModel.StopTimer();
                break;
            case nameof(MainViewModel.IsTranscribing):
                Log.Debug($"IsTranscribing changed to: {_viewModel.IsTranscribing}");
                _overlayWindow.ViewModel.IsTranscribing = _viewModel.IsTranscribing;
                if (_viewModel.IsTranscribing)
                {
                    _overlayWindow.ViewModel.StatusText = "Transcribing...";
                    _overlayWindow.ViewModel.StopTimer();
                }
                break;
            case nameof(MainViewModel.StatusText):
                Log.Debug($"StatusText changed to: {_viewModel.StatusText}");
                if (_viewModel.StatusText == "Copied to clipboard")
                {
                    _overlayWindow.ViewModel.StatusText = "Copied!";
                }
                else if (_viewModel.StatusText == "Cancelled")
                {
                    _overlayWindow.ViewModel.StatusText = "Cancelled";
                }
                else if (_viewModel.StatusText == "Refining prompt...")
                {
                    // Prompting adds a second network round-trip after transcription — show it
                    // on the pill so the extra wait doesn't read as a stuck "..."
                    _overlayWindow.ViewModel.StatusText = "Prompting…";
                }
                else
                {
                    _overlayWindow.ViewModel.StatusText = _viewModel.StatusText;
                }
                break;
        }
    }

    /// <summary>
    /// Mirrors the overlay's "Prompting" toggle into the MainViewModel so the transcription
    /// pipeline knows to expand this recording into a structured agent prompt.
    /// </summary>
    private void OnOverlayPromptModeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OverlayViewModel.IsPromptMode) && _overlayWindow?.ViewModel != null)
        {
            _viewModel.PromptMode = _overlayWindow.ViewModel.IsPromptMode;
            Log.Info($"Prompt mode toggled: {_viewModel.PromptMode}");
        }
    }

    /// <summary>
    /// Puts a spoken command on the pill and keeps it there: his words while it
    /// is sent, the daemon's answer when it comes, then a pause long enough to
    /// read it. A failure sits longer than a success, because it is the one
    /// worth reading.
    /// </summary>
    private void OnCommandProgress(object? sender, CommandProgressEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_overlayWindow == null || !_overlayWindow.IsLoaded) return;
            _commandLinger?.Stop();

            if (e.Stage == CommandStage.None)
            {
                _overlayWindow.ViewModel.ClearCommand();
                return;
            }

            _overlayWindow.ViewModel.CommandText = e.Text;
            _overlayWindow.ViewModel.CommandDetail = e.Detail;
            _overlayWindow.ViewModel.CommandStage = e.Stage;
            _overlayWindow.ViewModel.StopTimer();
            if (!_overlayWindow.IsVisible) _overlayWindow.Show();

            if (e.Stage is CommandStage.Sending or CommandStage.Working) return;

            _commandLinger ??= new System.Windows.Threading.DispatcherTimer();
            _commandLinger.Interval = TimeSpan.FromMilliseconds(
                e.Stage == CommandStage.Failed
                    ? Constants.VoiceCommandFailureLingerMs
                    : Constants.VoiceCommandResultLingerMs);
            _commandLinger.Tick -= OnCommandLingerElapsed;
            _commandLinger.Tick += OnCommandLingerElapsed;
            _commandLinger.Start();
        });
    }

    private void OnCommandLingerElapsed(object? sender, EventArgs e)
    {
        _commandLinger?.Stop();
        if (_overlayWindow?.ViewModel == null) return;
        // A new recording owns the pill now; leave it alone.
        if (_viewModel.IsListening || _viewModel.IsTranscribing) return;
        _overlayWindow.ViewModel.ClearCommand();
        _overlayWindow.Hide();
    }

    private void OnRequestHideOverlay(object? sender, EventArgs e)
    {
        Log.Info("RequestHideOverlay received");
        Dispatcher.Invoke(() =>
        {
            // Don't hide if we're currently listening - this prevents a stale hide request
            // from a previous session hiding the overlay for a new recording
            if (_viewModel.IsListening || _viewModel.IsTranscribing)
            {
                Log.Debug($"Skipping overlay hide - session still active (IsListening={_viewModel.IsListening}, IsTranscribing={_viewModel.IsTranscribing})");
                return;
            }

            // A command is still running, or its answer has only just appeared.
            // Hiding now would take the one thing he is waiting to read.
            if (_overlayWindow?.ViewModel.IsCommand == true)
            {
                Log.Debug($"Skipping overlay hide - command on the pill ({_overlayWindow.ViewModel.CommandStage})");
                return;
            }

            _overlayWindow?.ViewModel.StopTimer();
            _overlayWindow?.Hide();
            Log.Debug("OverlayWindow hidden");

            if (_viewModel is { } vm)
            {
                vm.PropertyChanged -= OnViewModelPropertyChanged;
            }
        });
    }

    private void OnRequestShowToast(object? sender, ToastEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            // The app usually sits hidden in the tray, where an in-app toast is invisible.
            // Warnings/errors (failed transcription, bad API key) must still reach the user,
            // so route them to a tray balloon when the window isn't showing.
            if (!IsVisible && e.Type == ToastType.Warning)
            {
                // Wrapped in try-catch due to known WPF visual tree race condition (see below).
                try
                {
                    TrayIcon.ShowBalloonTip("Talkty", e.Message,
                        Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning);
                }
                catch (Exception ex)
                {
                    Log.Warning($"Tray balloon failed: {ex.Message}");
                }
                return;
            }

            Toast.Show(e.Message, e.Type, e.DurationMs);
        });
    }

    private void OnRequestShowSettings(object? sender, EventArgs e)
    {
        Log.Info("RequestShowSettings received");
        Dispatcher.Invoke(() =>
        {
            try
            {
                // Always create a fresh settings window to avoid stale state
                Log.Debug("Creating new SettingsWindow");

                // Use shared services - this ensures settings and mic test use the same instances
                _settingsWindow = new SettingsWindow(_settingsService, _audioCaptureService);
                _settingsWindow.Owner = this;
                _settingsWindow.SettingsSaved += (s, settings) =>
                {
                    Log.Info("Settings saved, applying...");
                    _viewModel.ApplySettings(settings);

                    // Re-register hotkeys if they changed
                    var handle = new WindowInteropHelper(this).Handle;
                    RegisterConfiguredHotkey(handle);
                    RegisterCommandHotkey(handle);

                    // Update hotkey badge display
                    UpdateHotkeyBadge();
                };

                Log.Debug("Showing SettingsWindow");
                _settingsWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                Log.Error("Failed to show settings", ex);
            }
        });
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        Log.Info($"MainWindow closing. IsExiting: {_isExiting}");

        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
            Log.Info("Window hidden (minimized to tray)");
            return;
        }

        Log.Info("Cleaning up resources...");
        _hotkeyService.HotkeyPressed -= OnHotkeyPressed;
        _hotkeyService.CancelHotkeyPressed -= OnCancelHotkeyPressed;
        _hotkeyService.CommandHotkeyPressed -= OnCommandHotkeyPressed;
        _hotkeyService.Dispose();
        Log.Debug("HotkeyService disposed");

        _viewModel.RequestShowOverlay -= OnRequestShowOverlay;
        _viewModel.RequestHideOverlay -= OnRequestHideOverlay;
        _viewModel.CommandProgress -= OnCommandProgress;
        _commandLinger?.Stop();
        _viewModel.RequestShowSettings -= OnRequestShowSettings;
        _viewModel.RequestShowToast -= OnRequestShowToast;
        _viewModel.RecordingStarted -= OnRecordingStarted;
        _viewModel.RecordingStopped -= OnRecordingStopped;
        _viewModel.Dispose();
        Log.Debug("MainViewModel disposed");

        _overlayWindow?.Close();
        _settingsWindow?.Close();
        TrayIcon.Dispose();
        Log.Debug("TrayIcon disposed");

        Log.Info("MainWindow cleanup complete");
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        Log.Debug($"WindowState changed to: {WindowState}");
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            WindowState = WindowState.Normal;
            ShowTrayMinimizeHint();
            Log.Info("Window minimized to tray");
        }
    }

    private void ShowTrayMinimizeHint()
    {
        if (_hasShownTrayHint) return;

        _hasShownTrayHint = true;
        _settingsService.Settings.Hints.HasSeenTrayMinimizeHint = true;
        _settingsService.Save();

        // Show balloon tip from tray icon (wrapped in try-catch due to known WPF visual tree race condition)
        try
        {
            TrayIcon.ShowBalloonTip(
                "Talkty is still running",
                "The app is minimized to the system tray. Double-click the icon or use the hotkey to access it.",
                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
        }
        catch (ArgumentException ex)
        {
            Log.Warning($"Failed to show balloon tip (visual tree race condition): {ex.Message}");
        }
    }

    private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
    {
        Log.Info("Tray icon: Double-click");
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OpenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Log.Info("Tray menu: Open clicked");
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Log.Info("Tray menu: Settings clicked");
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _viewModel.OpenSettingsCommand.Execute(null);
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Log.Info("Tray menu: About clicked");
        var aboutWindow = new AboutWindow();
        aboutWindow.Owner = this;
        aboutWindow.ShowDialog();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Log.Info("Tray menu: Exit clicked");
        _isExiting = true;
        Close();
        Application.Current.Shutdown();
    }

    private void HistoryItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is TranscriptionHistoryItem item)
        {
            Log.Debug($"History item clicked: {item.Preview}");
            _viewModel.CopyHistoryItemCommand.Execute(item);
        }
    }

    // Copy just the spoken transcription from a prompted history entry (the "You said:" line).
    // Handled = true so the click doesn't also bubble to the whole-item (prompt) copy above.
    private void HistoryTranscription_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is TranscriptionHistoryItem item)
        {
            _viewModel.CopyHistoryTranscriptionCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        ShowTrayMinimizeHint();
        Log.Info("Window closed to tray");
    }

    private void HistoryList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Let focused action buttons receive Enter/Space themselves.
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase) return;
        if (HistoryList.SelectedItem is not TranscriptionHistoryItem item) return;
        if ((e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) ||
            (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control))
        {
            _viewModel.CopyHistoryItemCommand.Execute(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            var index = HistoryList.SelectedIndex;
            _viewModel.DeleteHistoryItemCommand.Execute(item);
            HistoryList.SelectedIndex = Math.Min(index, HistoryList.Items.Count - 1);
            e.Handled = true;
        }
    }
}
