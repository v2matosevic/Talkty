using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Talkty.App.Controls;
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
    private SettingsWindow? _settingsWindow;
    private bool _isExiting;
    private DateTime _lastHotkeyTime = DateTime.MinValue;
    private static readonly TimeSpan HotkeyDebounceInterval = TimeSpan.FromMilliseconds(500);
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

        // Initialize ViewModel
        Log.Info("Creating MainViewModel...");
        _viewModel = new MainViewModel(
            _settingsService,
            _audioCaptureService,
            transcriptionService,
            clipboardService,
            volumeDuckingService: volumeDuckingService);

        _viewModel.RequestShowOverlay += OnRequestShowOverlay;
        _viewModel.RequestHideOverlay += OnRequestHideOverlay;
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

        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.CancelHotkeyPressed += OnCancelHotkeyPressed;
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
                Toast.Show("Tip: Press the hotkey from any app to start recording", ToastNotification.ToastType.Tip, 4000);
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
            System.Windows.MessageBox.Show(
                $"Failed to register hotkey ({modifier} + {key}). It may be in use by another application.",
                "Talkty",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
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

                // Reset overlay state for new recording session
                _overlayWindow.ViewModel.IsListening = true;
                _overlayWindow.ViewModel.IsTranscribing = false;
                _overlayWindow.ViewModel.StatusText = "Listening...";
                _overlayWindow.ViewModel.StartTimer();
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
                break;
        }
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
            // Map ViewModel ToastType to Control ToastType
            var controlType = e.Type switch
            {
                ViewModels.ToastType.Success => Controls.ToastNotification.ToastType.Success,
                ViewModels.ToastType.Warning => Controls.ToastNotification.ToastType.Warning,
                ViewModels.ToastType.Tip => Controls.ToastNotification.ToastType.Tip,
                _ => Controls.ToastNotification.ToastType.Info
            };
            Toast.Show(e.Message, controlType, e.DurationMs);
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

                    // Re-register hotkey if it changed
                    var handle = new WindowInteropHelper(this).Handle;
                    RegisterConfiguredHotkey(handle);

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
        _hotkeyService.Dispose();
        Log.Debug("HotkeyService disposed");

        _viewModel.RequestShowOverlay -= OnRequestShowOverlay;
        _viewModel.RequestHideOverlay -= OnRequestHideOverlay;
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

        // Show balloon tip from tray icon
        TrayIcon.ShowBalloonTip(
            "Talkty is still running",
            "The app is minimized to the system tray. Double-click the icon or use the hotkey to access it.",
            Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
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
            Toast.Show("Copied to clipboard", ToastNotification.ToastType.Success, 2000);
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

    private void RecordButton_Click(object sender, MouseButtonEventArgs e)
    {
        _viewModel.ToggleListeningCommand.Execute(null);
    }
}
