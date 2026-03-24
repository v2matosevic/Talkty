using System.Runtime.InteropServices;

namespace Talkty.App.Services;

/// <summary>
/// Win32-based auto-paste service. Captures the foreground window before
/// recording starts, then restores focus and sends Ctrl+V after transcription.
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
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int ASFW_ANY = -1;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const ushort VK_MENU = 0x12;  // Alt key
    private const ushort VK_ESCAPE = 0x1B;
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

    #endregion

    private readonly Func<bool> _verifyClipboardHasText;
    private IntPtr _targetWindowHandle = IntPtr.Zero;

    /// <summary>
    /// Creates a new AutoPasteService.
    /// </summary>
    /// <param name="verifyClipboardHasText">
    /// A delegate that returns true when the clipboard contains text.
    /// Must handle any required thread marshalling (e.g., Dispatcher.Invoke) internally.
    /// </param>
    public AutoPasteService(Func<bool> verifyClipboardHasText)
    {
        _verifyClipboardHasText = verifyClipboardHasText;
    }

    /// <inheritdoc />
    public void CaptureTargetWindow()
    {
        _targetWindowHandle = GetForegroundWindow();
        Log.Debug($"Target window captured for auto-paste: {_targetWindowHandle}");
    }

    /// <inheritdoc />
    public void ClaimForegroundPrivilege()
    {
        // This MUST be called from the UI thread (which received the hotkey).
        // Windows grants SetForegroundWindow permission only to the thread
        // that last received user input. By calling this immediately when the
        // hotkey fires, we secure the privilege before transcription delays it.
        AllowSetForegroundWindow(ASFW_ANY);
        Log.Debug("Foreground privilege claimed from UI thread");
    }

    /// <inheritdoc />
    public void PasteToTargetWindow(Action? ensureClipboardText = null)
    {
        try
        {
            Log.Debug($"PasteToTargetWindow starting. Target: {_targetWindowHandle}");

            if (_targetWindowHandle == IntPtr.Zero || !IsWindow(_targetWindowHandle))
            {
                Log.Warning("Target window handle is invalid — text is on clipboard");
                return;
            }

            WaitForModifierKeysRelease();

            // === FOCUS RESTORE: ALT KEY TRICK ===
            // Pressing ALT causes Windows to enable SetForegroundWindow calls.
            // We immediately cancel it with ESC so no menu bar activates.
            EnableForegroundPermission();

            // Restore focus to target
            if (IsIconic(_targetWindowHandle))
            {
                ShowWindow(_targetWindowHandle, SW_RESTORE);
                Thread.Sleep(30);
            }

            ShowWindow(_targetWindowHandle, SW_SHOW);
            BringWindowToTop(_targetWindowHandle);
            var result = SetForegroundWindow(_targetWindowHandle);
            Log.Debug($"SetForegroundWindow result: {result}");

            // Verify + fallback with AttachThreadInput if needed
            Thread.Sleep(30);
            if (GetForegroundWindow() != _targetWindowHandle)
            {
                Log.Debug("Primary focus failed, trying AttachThreadInput fallback...");
                RestoreFocusWithThreadAttach();
            }

            if (GetForegroundWindow() != _targetWindowHandle)
            {
                Log.Warning("Failed to restore focus — skipping paste. Text is on clipboard for manual Ctrl+V.");
                return;
            }

            // Settle — let the target app fully activate
            Thread.Sleep(50);

            // Re-verify clipboard right before paste.
            // Focus switching can cause some apps to clear or claim the clipboard.
            if (ensureClipboardText != null)
            {
                try
                {
                    ensureClipboardText();
                    Log.Debug("Clipboard re-verified before paste");
                }
                catch (Exception ex)
                {
                    Log.Warning($"Clipboard re-set failed: {ex.Message}");
                }
            }

            SendCtrlV();
            Log.Info("Auto-paste completed successfully");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to simulate paste", ex);
        }
    }

    /// <summary>
    /// Enables SetForegroundWindow by sending ALT, then immediately cancels with ESC
    /// to prevent menu bar activation in the target app.
    /// </summary>
    private void EnableForegroundPermission()
    {
        var inputs = new INPUT[4];

        // ALT down — Windows enables SetForegroundWindow when ALT is pressed
        inputs[0] = new INPUT { type = INPUT_KEYBOARD };
        inputs[0].u.ki.wVk = VK_MENU;
        inputs[0].u.ki.dwFlags = 0;

        // ALT up
        inputs[1] = new INPUT { type = INPUT_KEYBOARD };
        inputs[1].u.ki.wVk = VK_MENU;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;

        // ESC down — cancel any menu that ALT may have activated
        inputs[2] = new INPUT { type = INPUT_KEYBOARD };
        inputs[2].u.ki.wVk = VK_ESCAPE;
        inputs[2].u.ki.dwFlags = 0;

        // ESC up
        inputs[3] = new INPUT { type = INPUT_KEYBOARD };
        inputs[3].u.ki.wVk = VK_ESCAPE;
        inputs[3].u.ki.dwFlags = KEYEVENTF_KEYUP;

        SendInput(4, inputs, INPUT.Size);
        Thread.Sleep(10); // Brief pause for Windows to process
        Log.Debug("Foreground permission enabled (ALT+ESC)");
    }

    /// <summary>
    /// Fallback focus restore using AttachThreadInput.
    /// </summary>
    private void RestoreFocusWithThreadAttach()
    {
        uint currentThreadId = GetCurrentThreadId();
        uint targetThreadId = GetWindowThreadProcessId(_targetWindowHandle, out _);

        bool attached = false;
        if (currentThreadId != targetThreadId)
        {
            attached = AttachThreadInput(currentThreadId, targetThreadId, true);
        }

        try
        {
            ShowWindow(_targetWindowHandle, SW_SHOW);
            BringWindowToTop(_targetWindowHandle);
            SetForegroundWindow(_targetWindowHandle);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThreadId, targetThreadId, false);
            }
        }
    }

    private bool VerifyClipboardReady()
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (_verifyClipboardHasText())
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
}
