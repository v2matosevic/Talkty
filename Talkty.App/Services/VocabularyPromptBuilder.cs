using System.Text;
using Talkty.App.Models;

namespace Talkty.App.Services;

/// <summary>Builds a short spelling hint from the saved vocabulary, not a rewriting instruction.</summary>
public static class VocabularyPromptBuilder
{
    // Whisper's byte-level tokenizer can use one token per UTF-8 byte. This conservative
    // budget stays below its 224-token prompt window even for unfamiliar names/Unicode.
    // Do not estimate tokens from character count or cut a term halfway through.
    internal const int MaxPromptBytes = 200;
    private static readonly HashSet<string> PresetTerms =
        new(DefaultVocabulary.CodingTerms, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PriorityPresetTerms =
        new(["Claude", "Anthropic", "kubectl", "PostgreSQL", "TypeScript"], StringComparer.OrdinalIgnoreCase);

    public static string? Build(AppSettings settings)
    {
        // Keep the existing language safeguard: English hints can pull multilingual
        // dictation toward English. Other engines do not consume this Whisper hint.
        if (!settings.UseCustomVocabulary || settings.AutoDetectLanguage ||
            !string.Equals(settings.Language, "en", StringComparison.OrdinalIgnoreCase) ||
            settings.ModelProfile.GetEngine() != TranscriptionEngine.Whisper)
            return null;

        var selected = new List<string>();
        int bytes = 0;
        foreach (var term in OrderedTerms(settings))
        {
            int cost = Encoding.UTF8.GetByteCount(term) + (selected.Count == 0 ? 0 : 2);
            if (bytes + cost > MaxPromptBytes) continue;
            selected.Add(term);
            bytes += cost;
        }

        return selected.Count == 0 ? null : string.Join(", ", selected);
    }

    /// <summary>
    /// Keyword hints for cloud models that take a term list (MAI-Transcribe 2). Same saved
    /// vocabulary and priority as <see cref="Build"/>, capped at what the model accepts. Not
    /// limited to English: these are spellings of names, not an English sentence.
    /// </summary>
    public static IReadOnlyList<string>? BuildCloudTerms(AppSettings settings)
    {
        if (!settings.UseCustomVocabulary)
            return null;

        var terms = OrderedTerms(settings).Take(Constants.CloudMaxVocabularyTerms).ToList();
        return terms.Count == 0 ? null : terms;
    }

    private static IEnumerable<string> OrderedTerms(AppSettings settings) =>
        (settings.CustomVocabulary ?? DefaultVocabulary.CodingTerms)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(term => string.Join(" ", term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(term => !term.Any(char.IsControl))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // User additions beat the large preloaded catalog, even when appended in Settings.
            // LINQ's stable ordering preserves the user's order within each priority.
            .OrderBy(term => !PresetTerms.Contains(term) ? 0 : PriorityPresetTerms.Contains(term) ? 1 : 2);
}
