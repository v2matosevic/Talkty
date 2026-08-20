using System.Windows;

namespace Talkty.App.Services;

public class ClipboardService : IClipboardService
{
    public bool SetText(string text)
    {
        const int maxRetries = 3;
        const int retryDelayMs = 50;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch
            {
                if (i < maxRetries - 1)
                {
                    Thread.Sleep(retryDelayMs);
                }
            }
        }

        return false;
    }

    public string? GetTextOrNull()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch
        {
            // Clipboard locked by another process — treat as "nothing to preserve".
            return null;
        }
    }
}
