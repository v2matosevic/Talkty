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
    /// Minutes without a transcription before the local model is unloaded to free RAM/VRAM
    /// (the app idles in the tray; a Large model otherwise holds 1.5-3 GB around the clock).
    /// Reload is transparent and overlaps with the next recording. 15 min keeps rapid-fire
    /// dictation sessions entirely on the loaded model.
    /// </summary>
    public const int ModelIdleUnloadMinutes = 15;

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
    /// Most vocabulary hints MAI-Transcribe 2 accepts per request: its phrase list answers a 51st
    /// term with a provider 400 (measured 2026-09-15).
    /// </summary>
    public const int CloudMaxVocabularyTerms = 50;

    /// <summary>
    /// MP3 bitrate for cloud uploads (16 kHz mono speech). 48 kbps matched WAV word for word on
    /// MAI-Transcribe 2 at a fifth of the size; 24-32 kbps started changing words.
    /// </summary>
    public const int CloudMp3BitRate = 48000;

    /// <summary>MAI speech uploads: Opus at 24 kbps, with MP3/WAV encoder fallback.</summary>
    public const int CloudOpusBitRate = 24000;

    /// <summary>
    /// Per-attempt timeout for the prompt-refinement LLM call (raw transcription → structured agent
    /// prompt), used when "Prompting" is enabled. Kept tight so a slow/stuck model in the fallback
    /// chain drops through to the next instead of hanging the user (a healthy call is ~1-3s).
    /// </summary>
    public const int PromptRefinementTimeoutMs = 12000;

    /// <summary>
    /// Completeness guard for prompt refinement. The whole point of "Prompting" is to EXPAND a
    /// dictation into a fuller, structured prompt — so a faithful rewrite of a substantial request is
    /// essentially always longer than the raw speech (it adds headings/bullets while keeping every
    /// detail). If a model instead returns something far SHORTER, it summarized and dropped the small
    /// load-bearing details — a fidelity failure we treat like any other and fall through to the next
    /// model in the chain. Only applied to substantial inputs so short asks (which legitimately produce
    /// short prompts) never trip it. Tuned from real logs: MiniMax M3 once returned 195 chars from a
    /// 578-char dictation (ratio 0.34) — caught; the Gemini family expanded (ratio &gt; 1) — passed.
    /// </summary>
    public const int PromptCompletenessMinInputChars = 400;

    /// <summary>
    /// Minimum acceptable output/input length ratio before a refinement is suspected of summarizing.
    /// Filler/repetition trimming shaves maybe 10-30%; dropping below 0.6 means real content was cut.
    /// </summary>
    public const double PromptCompletenessMinOutputRatio = 0.6;

    // ─── Prompt fidelity check (TypeSafe Jev via OpenRouter) ────────────
    // Bounds for the optional check that runs AFTER Prompting has produced a prompt. See
    // docs/PROMPT-FIDELITY.md. Every one of these is deliberately smaller than what the model or
    // the route would allow — the check is an advisory extra, never a second budget centre.

    /// <summary>
    /// Per-attempt deadline for one decision request. Matched to ADE's measured 501-848 ms
    /// evaluations with headroom; the check runs after delivery, so a slow answer costs nothing
    /// but is still cut off rather than left hanging.
    /// </summary>
    public const int JevDecisionTimeoutMs = 4000;

    /// <summary>Serialized request ceiling (the route's own documented state budget is larger).</summary>
    public const int JevMaxRequestBytes = 32_000;

    /// <summary>Response byte ceiling — a runaway body is refused rather than buffered.</summary>
    public const int JevMaxResponseBytes = 128_000;

    /// <summary>Upper bound on questions in one request.</summary>
    public const int JevMaxQuestions = 64;

    /// <summary>
    /// Source clauses the dictation is split into. More clauses means finer attribution and a
    /// bigger request; sentences beyond this are merged so the whole transcript stays covered.
    /// </summary>
    public const int JevMaxClauses = 12;

    /// <summary>Dictation length above which the model layer is skipped (code checks still run).</summary>
    public const int JevMaxTranscriptChars = 6_000;

    /// <summary>Generated-prompt length above which the model layer is skipped.</summary>
    public const int JevMaxRewriteChars = 12_000;

    /// <summary>
    /// Reserved before every outbound attempt, in millionths of a dollar. Replaced by the reported
    /// cost afterwards; an unreported cost keeps the reservation rather than counting as free.
    /// ADE's eight measured calls each cost about $0.0000245, so this is a conservative ceiling.
    /// </summary>
    public const long JevReservationMicroUsd = 2_000;

    /// <summary>Local daily allocation cap, in millionths of a dollar ($0.25 per local calendar day).</summary>
    public const long JevDailyBudgetMicroUsd = 250_000;

    /// <summary>Attempts allowed per rolling minute.</summary>
    public const int JevMaxAttemptsPerMinute = 20;

    /// <summary>An identical dictation/prompt pair is not re-evaluated within this window.</summary>
    public const int JevDuplicateSuppressionSeconds = 60;

    /// <summary>Comparison records retained locally (hashes and labels only, no text).</summary>
    public const int JevMaxRecords = 200;

    /// <summary>Most concerns shown at once — a toast the user cannot read helps nobody.</summary>
    public const int JevMaxSurfacedConcerns = 2;

    /// <summary>
    /// Hard ceiling on the assembled concern message. While Talkty sits in the tray — the normal
    /// case, because you are dictating into another app — a Warning toast is delivered as a Windows
    /// tray balloon, and the shell truncates NOTIFYICONDATA.szInfo at 256 characters WITHOUT
    /// telling anyone. A two-concern message with full-length quotes measured 326 characters, so it
    /// would have been cut off mid-sentence. Stay clear of the limit rather than discover it again.
    /// </summary>
    public const int FidelityToastMaxChars = 240;

    /// <summary>Quote budget per concern when only one is shown.</summary>
    public const int FidelityQuoteCharsSingle = 90;

    /// <summary>Quote budget per concern when several share the message.</summary>
    public const int FidelityQuoteCharsShared = 45;

    /// <summary>
    /// Minimum probability for the winning option before a clause concern is raised. PROVISIONAL:
    /// tuned on the development corpus only, never on the holdout, and not a measured accuracy.
    /// </summary>
    public const double JevFidelityMinProbability = 0.80;

    /// <summary>
    /// Minimum Choice confidence before a clause concern is raised. Confidence is a distribution
    /// statistic, not the probability that the answer is correct.
    /// </summary>
    public const double JevFidelityMinConfidence = 0.70;

    /// <summary>
    /// Minimum Noul yes-probability before reporting that the prompt added a requirement. Higher
    /// than the clause gate because Noul reports no confidence statistic to corroborate it.
    /// </summary>
    public const double JevFidelityMinAddedProbability = 0.85;

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

    /// <summary>
    /// Delay after a successful auto-paste before restoring the user's previous clipboard
    /// (when that option is on). Apps consume WM_PASTE synchronously within a few ms; 500ms
    /// leaves headroom for slow Electron apps. Known limitation: a target that reads the
    /// clipboard even later (stalled UI thread, some RDP sessions) would paste the restored
    /// old text — the restore path additionally verifies the clipboard still holds our
    /// transcription before touching it, which caps the damage to "no restore".
    /// </summary>
    public const int ClipboardRestoreDelayMs = 500;
}
