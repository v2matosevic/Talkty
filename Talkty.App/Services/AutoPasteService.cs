using System.Runtime.InteropServices;
using System.Text;

namespace Talkty.App.Services;

/// <summary>
/// Win32-based auto-paste service. Captures the foreground window when
/// recording stops, then sends the appropriate paste shortcut (Ctrl+V or
/// Ctrl+Shift+V for terminals) after transcription completes.
///
/// Design philosophy: minimize disruption to the target app's focus state.
/// The overlay is non-activating (WS_EX_NOACTIVATE), so the target window
/// stays focused throughout recording and transcription. In the common case,
/// we touch nothing — just send the keystroke.
/// </summary>
public class AutoPasteService : IAutoPasteService
{
    #region Win32 Interop

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
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

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
        public int rcCaretLeft;
        public int rcCaretTop;
        public int rcCaretRight;
        public int rcCaretBottom;
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ASFW_ANY = -1;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const ushort VK_MENU = 0x12;  // Alt key
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_ESCAPE = 0x1B;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;

        public static int Size => Marshal.SizeOf(typeof(INPUT));
    }

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

    #endregion

    private readonly Func<bool> _verifyClipboardHasText;
    private IntPtr _targetWindowHandle = IntPtr.Zero;

    public AutoPasteService(Func<bool> verifyClipboardHasText)
    {
        _verifyClipboardHasText = verifyClipboardHasText;
    }

    /// <inheritdoc />
    public void CaptureTargetWindow()
    {
        _targetWindowHandle = GetForegroundWindow();

        // Log full diagnostics about the target window for debugging paste issues
        var targetInfo = GetWindowDiagnostics(_targetWindowHandle);
        Log.Info($"Target captured — {targetInfo}");
    }

    /// <inheritdoc />
    public void ClaimForegroundPrivilege()
    {
        AllowSetForegroundWindow(ASFW_ANY);
        Log.Debug("Foreground privilege claimed from UI thread");
    }

    /// <inheritdoc />
    public void PasteToTargetWindow(Action? ensureClipboardText = null)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Log.Info($"=== AUTO-PASTE START === Target: {_targetWindowHandle}");

            if (_targetWindowHandle == IntPtr.Zero || !IsWindow(_targetWindowHandle))
            {
                Log.Warning("Target window handle is invalid — text is on clipboard");
                return;
            }

            // Log who currently has foreground (before we do anything)
            var currentFg = GetForegroundWindow();
            var currentFgInfo = GetWindowDiagnostics(currentFg);
            Log.Debug($"Current foreground before paste: {currentFgInfo}");

            WaitForModifierKeysRelease();
            Log.Debug($"[+{sw.ElapsedMilliseconds}ms] Modifier keys released");

            FlushModifierKeys();
            Log.Debug($"[+{sw.ElapsedMilliseconds}ms] Modifier keys flushed");

            // Check if the target is still the foreground window.
            // The overlay is non-activating, so in the normal case the target
            // stays focused throughout — no restoration needed.
            bool targetIsForeground = GetForegroundWindow() == _targetWindowHandle;

            if (!targetIsForeground)
            {
                var actualFg = GetForegroundWindow();
                Log.Debug($"Target lost foreground — actual foreground: {GetWindowDiagnostics(actualFg)}");
                if (!RestoreFocusToTarget())
                {
                    Log.Warning("Failed to restore focus — skipping paste. Text is on clipboard for manual Ctrl+V.");
                    return;
                }
                Thread.Sleep(60);
                Log.Debug($"[+{sw.ElapsedMilliseconds}ms] Focus restored");

                // Re-set clipboard ONLY after focus switch — switching focus can cause
                // some apps to clear or claim the clipboard.
                if (ensureClipboardText != null)
                {
                    try
                    {
                        ensureClipboardText();
                        Log.Debug($"[+{sw.ElapsedMilliseconds}ms] Clipboard re-set after focus restore");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"Clipboard re-set failed: {ex.Message}");
                    }
                }
            }
            else
            {
                Log.Debug($"[+{sw.ElapsedMilliseconds}ms] Target still has foreground — pasting directly");
            }

            // Identify the target window type for paste method selection
            var (windowClass, processName, isElevated) = GetWindowIdentity(_targetWindowHandle);
            Log.Info($"Target identity — class: \"{windowClass}\", process: \"{processName}\", elevated: {isElevated}");

            if (isElevated)
            {
                Log.Warning("Target is elevated (admin) — SendInput may be blocked by UIPI. Text is on clipboard.");
            }

            // Check if the hotkey's Alt key activated a menu bar in the target app.
            // Only send ESC to dismiss it if a menu is actually detected — avoids
            // blindly sending ESC which breaks Telegram (search), browsers (cancel), etc.
            DismissMenuBarIfActive();

            SendCtrlV();

            sw.Stop();
            Log.Info($"=== AUTO-PASTE END === Total: {sw.ElapsedMilliseconds}ms, class: \"{windowClass}\", process: \"{processName}\"");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to simulate paste", ex);
        }
    }

    /// <summary>
    /// Attempts to restore the target window to the foreground.
    /// Only called when the user switched apps during recording/transcription.
    /// Uses AttachThreadInput to bypass SetForegroundWindow restrictions.
    /// </summary>
    private bool RestoreFocusToTarget()
    {
        if (IsIconic(_targetWindowHandle))
        {
            ShowWindow(_targetWindowHandle, SW_RESTORE);
            Thread.Sleep(30);
        }

        // Attach threads so SetForegroundWindow bypasses the foreground lock
        uint currentThreadId = GetCurrentThreadId();
        uint targetThreadId = GetWindowThreadProcessId(_targetWindowHandle, out _);

        bool attached = false;
        if (currentThreadId != targetThreadId)
        {
            attached = AttachThreadInput(currentThreadId, targetThreadId, true);
            Log.Debug($"AttachThreadInput: {attached}");
        }

        try
        {
            BringWindowToTop(_targetWindowHandle);
            SetForegroundWindow(_targetWindowHandle);
            Thread.Sleep(30);

            if (GetForegroundWindow() == _targetWindowHandle)
            {
                Log.Debug("Focus restored (primary)");
                return true;
            }

            // Retry with ShowWindow
            ShowWindow(_targetWindowHandle, SW_SHOW);
            BringWindowToTop(_targetWindowHandle);
            SetForegroundWindow(_targetWindowHandle);
            Thread.Sleep(30);

            bool success = GetForegroundWindow() == _targetWindowHandle;
            Log.Debug(success ? "Focus restored (retry)" : $"Focus restore failed. Foreground: {GetForegroundWindow()}");
            return success;
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThreadId, targetThreadId, false);
            }
        }
    }

    /// <summary>
    /// Returns the window class name, process name, and elevation status for diagnostics.
    /// </summary>
    private (string windowClass, string processName, bool isElevated) GetWindowIdentity(IntPtr hWnd)
    {
        string windowClass = "";
        string processName = "";
        bool isElevated = false;

        var className = new StringBuilder(256);
        if (GetClassName(hWnd, className, className.Capacity) > 0)
            windowClass = className.ToString();

        GetWindowThreadProcessId(hWnd, out uint processId);
        if (processId != 0)
        {
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (hProcess != IntPtr.Zero)
            {
                try
                {
                    var exePath = new StringBuilder(1024);
                    int size = exePath.Capacity;
                    if (QueryFullProcessImageName(hProcess, 0, exePath, ref size))
                        processName = System.IO.Path.GetFileNameWithoutExtension(exePath.ToString());
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }
            else
            {
                // OpenProcess failed — likely the target is elevated (admin) and we're not.
                // UIPI will block SendInput to this window.
                isElevated = true;
                processName = "(access denied — likely elevated)";
            }
        }

        return (windowClass, processName, isElevated);
    }

    /// <summary>
    /// Returns a diagnostic string identifying a window (class + process).
    /// </summary>
    private string GetWindowDiagnostics(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "null";
        var (cls, proc, elevated) = GetWindowIdentity(hWnd);
        return $"hwnd={hWnd}, class=\"{cls}\", process=\"{proc}\"{(elevated ? " [ELEVATED]" : "")}";
    }

    /// <summary>
    /// Sends explicit key-up events for all modifier keys.
    /// Clears any lingering state from the hotkey so the paste
    /// is not corrupted (e.g., Alt+Ctrl+V instead of Ctrl+V).
    /// </summary>
    private void FlushModifierKeys()
    {
        var inputs = new INPUT[3];

        inputs[0] = new INPUT { type = INPUT_KEYBOARD };
        inputs[0].u.ki.wVk = VK_MENU;
        inputs[0].u.ki.dwFlags = KEYEVENTF_KEYUP;

        inputs[1] = new INPUT { type = INPUT_KEYBOARD };
        inputs[1].u.ki.wVk = VK_CONTROL;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;

        inputs[2] = new INPUT { type = INPUT_KEYBOARD };
        inputs[2].u.ki.wVk = VK_SHIFT;
        inputs[2].u.ki.dwFlags = KEYEVENTF_KEYUP;

        SendInput(3, inputs, INPUT.Size);
        Log.Debug("Modifier keys flushed");
    }

    private void WaitForModifierKeysRelease()
    {
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

    /// <summary>
    /// Checks if the target app has an active menu (caused by the hotkey's Alt key
    /// activating the menu bar). Only sends ESC if a menu is actually detected.
    /// This avoids blindly sending ESC to apps like Telegram (where ESC opens search)
    /// or browsers (where ESC cancels navigation).
    /// </summary>
    private void DismissMenuBarIfActive()
    {
        uint targetThreadId = GetWindowThreadProcessId(_targetWindowHandle, out _);
        var info = new GUITHREADINFO();
        info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));

        if (GetGUIThreadInfo(targetThreadId, ref info) && info.hwndMenuOwner != IntPtr.Zero)
        {
            Log.Debug($"Active menu detected (owner: {info.hwndMenuOwner}) — sending ESC to dismiss");

            var inputs = new INPUT[2];
            inputs[0] = new INPUT { type = INPUT_KEYBOARD };
            inputs[0].u.ki.wVk = VK_ESCAPE;
            inputs[1] = new INPUT { type = INPUT_KEYBOARD };
            inputs[1].u.ki.wVk = VK_ESCAPE;
            inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;

            SendInput(2, inputs, INPUT.Size);
            Thread.Sleep(15);
        }
        else
        {
            Log.Debug("No active menu detected — skipping ESC");
        }
    }

    private void SendCtrlV()
    {
        var inputs = new INPUT[4];

        inputs[0] = new INPUT { type = INPUT_KEYBOARD };
        inputs[0].u.ki.wVk = VK_CONTROL;

        inputs[1] = new INPUT { type = INPUT_KEYBOARD };
        inputs[1].u.ki.wVk = VK_V;

        inputs[2] = new INPUT { type = INPUT_KEYBOARD };
        inputs[2].u.ki.wVk = VK_V;
        inputs[2].u.ki.dwFlags = KEYEVENTF_KEYUP;

        inputs[3] = new INPUT { type = INPUT_KEYBOARD };
        inputs[3].u.ki.wVk = VK_CONTROL;
        inputs[3].u.ki.dwFlags = KEYEVENTF_KEYUP;

        var result = SendInput((uint)inputs.Length, inputs, INPUT.Size);
        if (result != inputs.Length)
        {
            var error = Marshal.GetLastWin32Error();
            Log.Error($"SendInput Ctrl+V failed. Sent: {result}/{inputs.Length}, Error: {error}");
        }
        else
        {
            Log.Debug($"Ctrl+V sent successfully ({result} inputs)");
        }
    }

}
