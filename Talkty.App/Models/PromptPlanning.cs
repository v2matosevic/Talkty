namespace Talkty.App.Models;

/// <summary>
/// Whether a fast decision runs BEFORE the refinement model, and how much it is allowed to change.
///
/// Persisted in settings.json BY NUMBER (there is no JsonStringEnumConverter) — never reorder or
/// remove members, or an existing user's saved mode silently becomes a different one.
/// </summary>
public enum PromptPlanning
{
    /// <summary>No classification. Prompting behaves exactly as it always has. = 0</summary>
    Off = 0,

    /// <summary>
    /// Classify and use the result only to help refinement: pass the request kind as a one-line
    /// hint and start substantial work on the higher-quality model. Every dictation is still
    /// refined, so the delivered prompt can only get better or stay the same. = 1
    /// </summary>
    Hints = 1,

    /// <summary>
    /// As Hints, plus: when the dictation is confidently already a usable prompt, skip the
    /// refinement model entirely and deliver the cleaned transcription. Saves the call and the
    /// wait on the common one-line ask. This is the only mode that changes what you receive. = 2
    /// </summary>
    Full = 2
}
