using System.Windows;

namespace Talkty.App.Services;

public class ClipboardService : IClipboardService
{
    public bool SetText(string text)
    {
        const int maxRetries = 3;
        const int retryDelayMs = 100;

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
}
