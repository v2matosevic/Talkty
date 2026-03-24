namespace Talkty.App;

/// <summary>
/// Application-wide constants. Centralizes magic numbers to make tuning
/// and reasoning about timing/audio behavior straightforward.
/// </summary>
public static class Constants
{
    /// <summary>
    /// Minimum interval between hotkey activations to prevent double-fires.
    /// </summary>
    public const int HotkeyDebounceMs = 500;

    /// <summary>
    /// Delay before resetting status text back to "Ready" after a cancel or completion.
    /// </summary>
    public const int StatusResetDelayMs = 1000;

    /// <summary>
    /// Maximum number of transcription history entries retained in memory and on disk.
    /// </summary>
    public const int MaxHistoryEntries = 50;

    /// <summary>
    /// Audio sample rate expected by the Whisper engine, in Hz.
    /// </summary>
    public const int SampleRate = 16000;
}
