namespace Talkty.App.Services;

public interface IClipboardService
{
    bool SetText(string text);

    /// <summary>
    /// Returns the clipboard's current text content, or null when the clipboard holds
    /// no text (empty, image, files, …) or is locked by another process.
    /// Must be called on the UI thread.
    /// </summary>
    string? GetTextOrNull();
}
