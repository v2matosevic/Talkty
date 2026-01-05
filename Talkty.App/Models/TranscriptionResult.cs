namespace Talkty.App.Models;

public class TranscriptionResult
{
    public string Text { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public TimeSpan Duration { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}
