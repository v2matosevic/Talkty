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
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>GUITHREADINFO.flags bit: set when the caret is actually visible.</summary>
    private const uint GUI_CARETBLINKING = 0x1;

    /// <summary>Vertical gap (device px) between the caret line / mouse pointer and the pill.</summary>
    private const int NearCursorOffsetPx = 28;

    private static readonly Random _random = new();

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

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    public OverlayViewModel ViewModel { get; }

    /// <summary>
    /// When true, the pill is positioned near the target app's text caret (fallback:
    /// mouse pointer, then bottom-center of the active monitor). Set from settings by
    /// MainWindow before each Show.
    /// </summary>
    public bool PositionNearTextCursor { get; set; }

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
        PositionOverlay();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Reposition every time the overlay becomes visible (user may have switched monitors)
        if (e.NewValue is true && IsLoaded)
        {
            PositionOverlay();
        }
    }

    /// <summary>
    /// Positions the pill for this recording session: near the text caret when enabled
    /// (that's where the user is working), else near the mouse pointer, else the classic
    /// bottom-center of the monitor the cursor is on.
    /// </summary>
    private void PositionOverlay()
    {
        try
        {
            if (PositionNearTextCursor)
            {
                if (TryPositionNearCaret()) return;
                if (TryPositionNearMouse()) return;
            }
            PositionOnActiveMonitor();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to position overlay", ex);
            PositionOnPrimaryScreen();
        }
    }

    /// <summary>
    /// Places the pill just below the focused app's text caret. Works for apps that use
    /// the Win32 caret (native edit controls, terminals, many browsers); Electron apps
    /// often don't expose one — those fall back to the mouse position.
    /// </summary>
    private bool TryPositionNearCaret()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        var threadId = GetWindowThreadProcessId(foreground, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(threadId, ref info)) return false;
        // Require a VISIBLE caret — apps can keep a caret registered (hwndCaret set) while
        // it's hidden, and its stale rect would anchor the pill somewhere meaningless.
        if (info.hwndCaret == IntPtr.Zero || (info.flags & GUI_CARETBLINKING) == 0) return false;

        // rcCaret is in client coordinates of hwndCaret — convert to screen.
        var topLeft = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
        var bottomRight = new POINT { X = info.rcCaret.Right, Y = info.rcCaret.Bottom };
        if (!ClientToScreen(info.hwndCaret, ref topLeft) || !ClientToScreen(info.hwndCaret, ref bottomRight))
            return false;

        // A collapsed/zero rect means the app registered a caret but isn't showing one.
        if (bottomRight.Y - topLeft.Y <= 0) return false;

        PositionAtScreenAnchor(topLeft.X, topLeft.Y, bottomRight.Y);
        Log.Debug($"Overlay positioned near caret at screen ({topLeft.X}, {bottomRight.Y})");
        return true;
    }

    private bool TryPositionNearMouse()
    {
        if (!GetCursorPos(out var cursor)) return false;
        PositionAtScreenAnchor(cursor.X, cursor.Y, cursor.Y);
        Log.Debug($"Overlay positioned near mouse at screen ({cursor.X}, {cursor.Y})");
        return true;
    }

    /// <summary>
    /// Centers the pill horizontally on <paramref name="anchorX"/> and puts it just below
    /// <paramref name="anchorBottomY"/> (all device px). Flips above the anchor when there
    /// is no room below, and clamps to the work area of the anchor's monitor.
    /// </summary>
    private void PositionAtScreenAnchor(int anchorX, int anchorTopY, int anchorBottomY)
    {
        var anchor = new POINT { X = anchorX, Y = anchorBottomY };
        var hMonitor = MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (hMonitor == IntPtr.Zero || !GetMonitorInfo(hMonitor, ref monitorInfo))
        {
            PositionOnPrimaryScreen();
            return;
        }

        var (dpiX, dpiY) = GetDpiScale();
        var work = monitorInfo.rcWork;

        // Everything in WPF units from here on.
        double workLeft = work.Left / dpiX, workTop = work.Top / dpiY;
        double workRight = work.Right / dpiX, workBottom = work.Bottom / dpiY;

        double left = anchorX / dpiX - ActualWidth / 2;
        double top = anchorBottomY / dpiY + NearCursorOffsetPx / dpiY;

        // No room below the anchor → flip above it.
        if (top + ActualHeight > workBottom)
        {
            top = anchorTopY / dpiY - NearCursorOffsetPx / dpiY - ActualHeight;
        }

        Left = Math.Clamp(left, workLeft, Math.Max(workLeft, workRight - ActualWidth));
        Top = Math.Clamp(top, workTop, Math.Max(workTop, workBottom - ActualHeight));
    }

    private (double dpiX, double dpiY) GetDpiScale()
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            return (source.CompositionTarget.TransformToDevice.M11,
                    source.CompositionTarget.TransformToDevice.M22);
        }

        using var graphics = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        return (graphics.DpiX / 96.0, graphics.DpiY / 96.0);
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
        // Already on UI thread — PropertyChanged fires on UI thread after our InvokeAsync fix.
        // No Dispatcher.Invoke needed; execute directly.
        var baseHeight = 6.0;
        var maxHeight = 14.0;
        var range = maxHeight - baseHeight;

        if (Bar1 != null)
            Bar1.Height = baseHeight + (range * level * (0.7 + _random.NextDouble() * 0.3));
        if (Bar2 != null)
            Bar2.Height = baseHeight + 2 + (range * level * (0.9 + _random.NextDouble() * 0.2));
        if (Bar3 != null)
            Bar3.Height = baseHeight + (range * level * (0.6 + _random.NextDouble() * 0.3));
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
            var (dpiX, dpiY) = GetDpiScale();

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
