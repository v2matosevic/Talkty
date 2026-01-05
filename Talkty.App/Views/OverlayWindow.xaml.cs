using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Talkty.App.Services;
using Talkty.App.ViewModels;

namespace Talkty.App.Views;

public partial class OverlayWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public OverlayViewModel ViewModel { get; }

    public OverlayWindow()
    {
        InitializeComponent();

        ViewModel = new OverlayViewModel();
        DataContext = ViewModel;

        // Subscribe to audio level changes for bar animation
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Make the window not steal focus (WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW)
        var hwnd = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        Log.Debug("Overlay window set to WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Position after layout is computed (SizeToContent needs this)
        PositionOnActiveMonitor();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Reposition every time the overlay becomes visible (user may have switched monitors)
        if (e.NewValue is true && IsLoaded)
        {
            PositionOnActiveMonitor();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OverlayViewModel.AudioLevel))
        {
            UpdateAudioBars(ViewModel.AudioLevel);
        }
    }

    private void UpdateAudioBars(float level)
    {
        // Level is 0.0 to 1.0
        // Animate bar heights based on audio level
        var baseHeight = 6.0;
        var maxHeight = 14.0;
        var range = maxHeight - baseHeight;

        // Add some randomness for visual effect
        var random = new Random();

        Dispatcher.Invoke(() =>
        {
            if (Bar1 != null)
            {
                var variation = random.NextDouble() * 0.3;
                Bar1.Height = baseHeight + (range * level * (0.7 + variation));
            }
            if (Bar2 != null)
            {
                var variation = random.NextDouble() * 0.2;
                Bar2.Height = baseHeight + 2 + (range * level * (0.9 + variation));
            }
            if (Bar3 != null)
            {
                var variation = random.NextDouble() * 0.3;
                Bar3.Height = baseHeight + (range * level * (0.6 + variation));
            }
        });
    }

    private void PositionOnActiveMonitor()
    {
        try
        {
            // Get cursor position
            if (!GetCursorPos(out var cursorPos))
            {
                PositionOnPrimaryScreen();
                return;
            }

            // Get monitor containing cursor
            var hMonitor = MonitorFromPoint(cursorPos, MONITOR_DEFAULTTONEAREST);
            if (hMonitor == IntPtr.Zero)
            {
                PositionOnPrimaryScreen();
                return;
            }

            // Get monitor info
            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMonitor, ref monitorInfo))
            {
                PositionOnPrimaryScreen();
                return;
            }

            // Get work area (excludes taskbar)
            var workArea = monitorInfo.rcWork;
            var workWidth = workArea.Right - workArea.Left;
            var workHeight = workArea.Bottom - workArea.Top;

            // Get DPI scaling for this window
            var source = PresentationSource.FromVisual(this);
            double dpiX, dpiY;

            if (source?.CompositionTarget != null)
            {
                dpiX = source.CompositionTarget.TransformToDevice.M11;
                dpiY = source.CompositionTarget.TransformToDevice.M22;
            }
            else
            {
                using var graphics = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
                dpiX = graphics.DpiX / 96.0;
                dpiY = graphics.DpiY / 96.0;
            }

            // Convert to WPF units (device-independent pixels)
            var wpfWorkWidth = workWidth / dpiX;
            var wpfWorkHeight = workHeight / dpiY;
            var wpfWorkLeft = workArea.Left / dpiX;
            var wpfWorkTop = workArea.Top / dpiY;

            // Position at bottom center of the monitor's work area
            // Use ActualWidth/Height since we have SizeToContent
            Left = wpfWorkLeft + (wpfWorkWidth - ActualWidth) / 2;
            Top = wpfWorkTop + wpfWorkHeight - ActualHeight - 40;

            Log.Debug($"Overlay positioned at ({Left:F0}, {Top:F0}) on monitor work area");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to position overlay on active monitor", ex);
            PositionOnPrimaryScreen();
        }
    }

    private void PositionOnPrimaryScreen()
    {
        var screen = SystemParameters.WorkArea;
        Left = (screen.Width - ActualWidth) / 2;
        Top = screen.Height - ActualHeight - 40;
        Log.Debug($"Overlay positioned at ({Left:F0}, {Top:F0}) on primary screen");
    }
}
