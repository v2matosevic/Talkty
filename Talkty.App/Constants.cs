namespace Talkty.App;

/// <summary>
/// Application-wide constants. Centralizes magic numbers to make tuning
/// and reasoning about timing/audio behavior straightforward.
/// </summary>
public static class Constants
{
    // ─── Hotkey / status ────────────────────────────────────────────────

    /// <summary>
    /// Minimum interval between hotkey activations to prevent double-fires.
    /// </summary>
    public const int HotkeyDebounceMs = 500;

    /// <summary>
    /// Delay before resetting status text back to "Ready" after a cancel or completion.
    /// </summary>
    public const int StatusResetDelayMs = 1000;

    // ─── History ────────────────────────────────────────────────────────

    /// <summary>
    /// Maximum number of transcription history entries retained in memory and on disk.
    /// </summary>
    public const int MaxHistoryEntries = 50;

    // ─── Audio ──────────────────────────────────────────────────────────

    /// <summary>
    /// Audio sample rate expected by the Whisper engine, in Hz.
    /// </summary>
    public const int SampleRate = 16000;

    /// <summary>
    /// Timeout for awaiting NAudio's in-flight buffer flush on StopRecording.
    /// Without this wait, the last 200-400ms of speech is lost.
    /// </summary>
    public const int RecordingFlushTimeoutMs = 500;

    // ─── Silence trim ───────────────────────────────────────────────────

    /// <summary>
    /// RMS energy threshold below which audio is considered silence for trimming.
    /// </summary>
    public const float SilenceThreshold = 0.01f;

    /// <summary>
    /// Window size in samples for silence detection (100ms at 16kHz).
    /// </summary>
    public const int SilenceWindowSamples = SampleRate / 10;

    /// <summary>
    /// Safety margin preserved on each end of trimmed audio (200ms at 16kHz).
    /// Protects against trimming quiet trail-offs mid-word.
    /// </summary>
    public const int SilenceMarginSamples = SampleRate / 5;

    // ─── Whisper ────────────────────────────────────────────────────────

    /// <summary>
    /// Upper bound on Whisper threads — whisper.cpp is compute-bound, gains flatten beyond this.
    /// </summary>
    public const int WhisperMaxThreads = 8;

    /// <summary>
    /// Silent-sample count used to warm up the processor on model load (0.5s at 16kHz).
    /// </summary>
    public const int WhisperWarmupSamples = SampleRate / 2;

    // ─── Cloud transcription (OpenRouter) ───────────────────────────────

    /// <summary>
    /// Timeout for a single cloud transcription request. Longer than the local 30s default
    /// because it covers network round-trip, provider queue, and remote inference.
    /// ESC still cancels immediately via the caller's token, independent of this.
    /// </summary>
    public const int CloudTranscriptionTimeoutMs = 60000;

    /// <summary>
    /// Soft limit on audio length per cloud request. OpenRouter's upstream provider caps a
    /// single file around 60s — beyond this we warn rather than silently truncate.
    /// </summary>
    public const int CloudMaxAudioSeconds = 55;

    /// <summary>
    /// Per-attempt timeout for the prompt-refinement LLM call (raw transcription → structured agent
    /// prompt), used when "Prompting" is enabled. Kept tight so a slow/stuck model in the fallback
    /// chain drops through to the next instead of hanging the user (a healthy call is ~1-3s).
    /// </summary>
    public const int PromptRefinementTimeoutMs = 12000;

    // ─── Auto-paste ─────────────────────────────────────────────────────

    /// <summary>
    /// Maximum time to wait for the user to release modifier keys before pasting.
    /// </summary>
    public const int PasteModifierReleaseTimeoutMs = 500;

    /// <summary>
    /// Polling interval while waiting for modifier key release.
    /// </summary>
    public const int PasteModifierPollMs = 10;

    /// <summary>
    /// Short sleep between focus-restore attempts on the rare path where the user
    /// switched apps during recording.
    /// </summary>
    public const int PasteFocusRestoreDelayMs = 30;

    /// <summary>
    /// Longer sleep after a failed focus-restore attempt before retrying.
    /// </summary>
    public const int PasteFocusRetryDelayMs = 60;
}
