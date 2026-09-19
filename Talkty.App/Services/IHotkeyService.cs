using System.Windows.Input;
using Talkty.App.Models;

namespace Talkty.App.Services;

public interface IHotkeyService : IDisposable
{
    event EventHandler? HotkeyPressed;
    event EventHandler? CancelHotkeyPressed;

    /// <summary>The second hotkey: record as usual, but treat it as a command.</summary>
    event EventHandler? CommandHotkeyPressed;
    bool Register(nint windowHandle, uint modifiers, uint key);
    bool Register(nint windowHandle, HotkeyModifiers modifiers, Key key);
    void Unregister();

    /// <summary>
    /// Registers the command-mode hotkey (default Alt+W) alongside the dictation one.
    /// </summary>
    bool RegisterCommandHotkey(nint windowHandle, HotkeyModifiers modifiers, Key key);

    /// <summary>Unregisters the command-mode hotkey.</summary>
    void UnregisterCommandHotkey();

    /// <summary>
    /// Registers ESC key as cancel hotkey (used during recording)
    /// </summary>
    bool RegisterCancelHotkey(nint windowHandle);

    /// <summary>
    /// Unregisters the cancel hotkey
    /// </summary>
    void UnregisterCancelHotkey();
}
