namespace Talkty.App.Services;

/// <summary>
/// Transforms a raw transcription into a polished, structured prompt for a coding AI agent.
/// Backed by an LLM via OpenRouter chat completions. Opt-in per recording via the overlay's
/// "Prompting" toggle. Independent of the transcription engine (local or cloud) — only the
/// refinement step needs the OpenRouter key.
/// </summary>
public interface IPromptRefinementService
{
    /// <summary>Whether an API key is configured (refinement is possible).</summary>
    bool IsConfigured { get; }

    /// <summary>Sets the (decrypted) OpenRouter API key. Pass null to clear.</summary>
    void SetApiKey(string? apiKey);

    /// <summary>
    /// Sets the preferred primary refinement model (OpenRouter slug). Null/empty restores the default.
    /// The remaining built-in models stay as automatic fallbacks beneath the chosen one.
    /// </summary>
    void SetModel(string? modelSlug);

    /// <summary>
    /// Expands the given transcription into a full, structured coding-agent prompt.
    /// Returns null on failure (caller should fall back to the raw transcription).
    /// </summary>
    Task<string?> RefineAsync(string transcription, CancellationToken cancellationToken = default);

    /// <summary>
    /// User-facing description of why the last <see cref="RefineAsync"/> returned null
    /// (invalid key, out of credits, all models failed). Null after a success or a user cancel.
    /// </summary>
    string? LastError { get; }
}
