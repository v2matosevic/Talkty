namespace Talkty.App.Models;

/// <summary>
/// How the prompt-fidelity check behaves after Prompting rewrites a dictation.
///
/// Persisted in settings.json BY NUMBER (there is no JsonStringEnumConverter) — never reorder or
/// remove members, or an existing user's saved mode silently becomes a different one.
/// </summary>
public enum PromptFidelityMode
{
    /// <summary>No check runs and no request is ever sent. = 0</summary>
    Off = 0,

    /// <summary>
    /// The check runs and keeps a local comparison record, but shows the user nothing and changes
    /// nothing about the delivered prompt. This is the shadow-evaluation mode. = 1
    /// </summary>
    RecordOnly = 1,

    /// <summary>
    /// As Record only, plus: a concern that clears the policy thresholds is shown after the prompt
    /// has already been delivered, quoting the user's own words and a fixed label. = 2
    /// </summary>
    Review = 2
}
