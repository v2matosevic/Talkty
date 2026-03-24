namespace Talkty.App.Services;

/// <summary>
/// Handles capturing a target window before recording and pasting
/// transcribed text into it after transcription completes.
/// </summary>
public interface IAutoPasteService
{
    /// <summary>
    /// Captures the currently focused window so it can be restored later.
    /// Call this before showing any overlay or recording UI.
    /// </summary>
    void CaptureTargetWindow();

    /// <summary>
    /// Claims the foreground window privilege. MUST be called from the UI thread
    /// (which received the hotkey input) before transcription starts.
    /// Windows only grants SetForegroundWindow permission to the thread that
    /// last received user input — calling this later from a thread pool thread fails.
    /// </summary>
    void ClaimForegroundPrivilege();

    /// <summary>
    /// Restores focus to the previously captured window and sends Ctrl+V.
    /// This method is blocking and should be called from a background thread.
    /// </summary>
    /// <param name="ensureClipboardText">
    /// Delegate that re-sets the clipboard text if it was cleared during focus restore.
    /// Must handle Dispatcher marshalling internally. Called right before Ctrl+V.
    /// </param>
    void PasteToTargetWindow(Action? ensureClipboardText = null);
}
