using System.Windows.Input;
using Talkty.App.Models;

namespace Talkty.App.Services;

public interface IHotkeyService : IDisposable
{
    event EventHandler? HotkeyPressed;
    event EventHandler? CancelHotkeyPressed;
    bool Register(nint windowHandle, uint modifiers, uint key);
    bool Register(nint windowHandle, HotkeyModifiers modifiers, Key key);
    void Unregister();

    /// <summary>
    /// Registers ESC key as cancel hotkey (used during recording)
    /// </summary>
    bool RegisterCancelHotkey(nint windowHandle);

    /// <summary>
    /// Unregisters the cancel hotkey
    /// </summary>
    void UnregisterCancelHotkey();
}
