namespace Talkty.App.Services;

/// <summary>
/// Result of an auto-paste attempt. Anything other than <see cref="Pasted"/> means the
/// text is still on the clipboard for a manual Ctrl+V — the caller should tell the user
/// instead of failing silently.
/// </summary>
public enum PasteOutcome
{
    /// <summary>Ctrl+V was delivered to the target window.</summary>
    Pasted,

    /// <summary>The captured target window no longer exists.</summary>
    NoTarget,

    /// <summary>The user switched apps and focus could not be restored to the target.</summary>
    FocusRestoreFailed,

    /// <summary>
    /// The target runs elevated (as administrator) — Windows UIPI silently discards
    /// keystrokes sent from a non-elevated process, so the paste almost certainly
    /// did not arrive.
    /// </summary>
    TargetElevated,

    /// <summary>An unexpected error occurred while pasting.</summary>
    Failed,
}

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
    PasteOutcome PasteToTargetWindow(Action? ensureClipboardText = null);
}
