using Talkty.App.Models;

namespace Talkty.App.Services;

/// <summary>Raised only in <see cref="PromptFidelityMode.Review"/>, after delivery is complete.</summary>
public sealed class FidelityConcernEventArgs : EventArgs
{
    public required IReadOnlyList<FidelityConcern> Concerns { get; init; }
}

/// <summary>
/// Checks a generated coding-agent prompt against the dictation it came from, and reports
/// bounded concerns. It never rewrites the prompt, never changes which refinement model was
/// used, and never delays clipboard or paste — a failed or uncertain judgment leaves the
/// established flow exactly as it was.
/// </summary>
public interface IPromptFidelityService
{
    /// <summary>Off, Record only, or Review. Off means no request is ever sent.</summary>
    PromptFidelityMode Mode { get; set; }

    /// <summary>Sets the (decrypted) OpenRouter API key — the same connection Prompting already uses.</summary>
    void SetApiKey(string? apiKey);

    /// <summary>
    /// Evaluates one dictation/rewrite pair. Safe to fire and forget: it owns its deadline and
    /// budget, runs off the UI thread, and swallows every transport failure.
    /// </summary>
    Task<PromptFidelityOutcome> EvaluateAsync(string transcript, string rewrite);

    /// <summary>Cancels an in-flight evaluation (ESC, or a new recording starting).</summary>
    void CancelPending();

    /// <summary>Fired when Review mode has a concern worth showing. Never fired in Off or Record only.</summary>
    event EventHandler<FidelityConcernEventArgs>? ConcernRaised;
}
