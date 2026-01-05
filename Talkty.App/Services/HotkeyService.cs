using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Talkty.App.Models;

namespace Talkty.App.Services;

public class HotkeyService : IHotkeyService
{
    private const int HOTKEY_ID = 9000;
    private const int CANCEL_HOTKEY_ID = 9001;
    private const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const uint VK_Q = 0x51;
    public const uint VK_ESCAPE = 0x1B;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    private nint _windowHandle;
    private HwndSource? _source;
    private bool _isRegistered;
    private bool _isCancelRegistered;
    private bool _hookAdded;

    public event EventHandler? HotkeyPressed;
    public event EventHandler? CancelHotkeyPressed;

    public bool Register(nint windowHandle, uint modifiers, uint key)
    {
        _windowHandle = windowHandle;

        if (!_hookAdded)
        {
            _source = HwndSource.FromHwnd(windowHandle);
            _source?.AddHook(WndProc);
            _hookAdded = true;
        }

        _isRegistered = RegisterHotKey(windowHandle, HOTKEY_ID, modifiers | MOD_NOREPEAT, key);
        return _isRegistered;
    }

    public bool Register(nint windowHandle, HotkeyModifiers modifiers, Key key)
    {
        // Unregister existing hotkey first
        if (_isRegistered)
        {
            UnregisterHotKey(_windowHandle, HOTKEY_ID);
            _isRegistered = false;
        }

        _windowHandle = windowHandle;

        if (!_hookAdded)
        {
            _source = HwndSource.FromHwnd(windowHandle);
            _source?.AddHook(WndProc);
            _hookAdded = true;
        }

        // Convert HotkeyModifiers to Win32 modifiers
        uint mods = MOD_NOREPEAT;
        if (modifiers.HasFlag(HotkeyModifiers.Alt))
            mods |= MOD_ALT;
        if (modifiers.HasFlag(HotkeyModifiers.Ctrl))
            mods |= MOD_CONTROL;
        if (modifiers.HasFlag(HotkeyModifiers.Shift))
            mods |= MOD_SHIFT;
        if (modifiers.HasFlag(HotkeyModifiers.Win))
            mods |= MOD_WIN;

        // Convert WPF Key to Virtual Key code
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);

        Log.Info($"Registering hotkey: modifiers=0x{mods:X4}, vk=0x{vk:X2} ({key})");
        _isRegistered = RegisterHotKey(windowHandle, HOTKEY_ID, mods, vk);

        if (_isRegistered)
        {
            Log.Info($"Hotkey registered successfully");
        }
        else
        {
            Log.Error($"Failed to register hotkey");
        }

        return _isRegistered;
    }

    public void Unregister()
    {
        if (_isRegistered && _windowHandle != nint.Zero)
        {
            UnregisterHotKey(_windowHandle, HOTKEY_ID);
            _isRegistered = false;
        }

        if (_hookAdded)
        {
            _source?.RemoveHook(WndProc);
            _source = null;
            _hookAdded = false;
        }
    }

    public bool RegisterCancelHotkey(nint windowHandle)
    {
        if (_isCancelRegistered)
        {
            Log.Debug("Cancel hotkey already registered");
            return true;
        }

        _windowHandle = windowHandle;

        if (!_hookAdded)
        {
            _source = HwndSource.FromHwnd(windowHandle);
            _source?.AddHook(WndProc);
            _hookAdded = true;
        }

        // Register ESC with no modifiers
        _isCancelRegistered = RegisterHotKey(windowHandle, CANCEL_HOTKEY_ID, MOD_NOREPEAT, VK_ESCAPE);

        if (_isCancelRegistered)
        {
            Log.Info("Cancel hotkey (ESC) registered successfully");
        }
        else
        {
            Log.Warning("Failed to register cancel hotkey (ESC) - may be in use");
        }

        return _isCancelRegistered;
    }

    public void UnregisterCancelHotkey()
    {
        if (_isCancelRegistered && _windowHandle != nint.Zero)
        {
            UnregisterHotKey(_windowHandle, CANCEL_HOTKEY_ID);
            _isCancelRegistered = false;
            Log.Debug("Cancel hotkey (ESC) unregistered");
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            var hotkeyId = wParam.ToInt32();
            if (hotkeyId == HOTKEY_ID)
            {
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
                handled = true;
            }
            else if (hotkeyId == CANCEL_HOTKEY_ID)
            {
                CancelHotkeyPressed?.Invoke(this, EventArgs.Empty);
                handled = true;
            }
        }
        return nint.Zero;
    }

    public void Dispose()
    {
        UnregisterCancelHotkey();
        Unregister();
        GC.SuppressFinalize(this);
    }
}
