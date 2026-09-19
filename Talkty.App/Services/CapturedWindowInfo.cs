namespace Talkty.App.Services;

/// <summary>Immutable metadata captured at recording stop, before transcription.</summary>
public record CapturedWindowInfo(
    string Handle,
    int ProcessId,
    string ProcessName,
    string Title,
    string? ProcessStartedAt);
