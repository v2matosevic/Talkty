using System.Linq;
using System.Text.RegularExpressions;

namespace Talkty.App.Services;

/// <summary>What kind of fidelity concern a finding represents.</summary>
public enum FidelityConcernKind
{
    /// <summary>An exact numeric literal from the dictation is absent from the rewrite (code check).</summary>
    MissingNumber,
    /// <summary>An exact code identifier / file name from the dictation is absent from the rewrite (code check).</summary>
    MissingIdentifier,
    /// <summary>The dictation contained a prohibition and the rewrite contains none at all (code check).</summary>
    DroppedProhibition,
    /// <summary>A source clause the model judged omitted.</summary>
    OmittedClause,
    /// <summary>A source clause the model judged contradicted by the rewrite.</summary>
    ContradictedClause,
    /// <summary>The rewrite appears to state a requirement the dictation did not contain.</summary>
    AddedRequirement
}

/// <summary>
/// One concern worth the speaker's attention. <see cref="SourceText"/> is a VERBATIM span of the
/// user's own dictation (or, for <see cref="FidelityConcernKind.AddedRequirement"/>, empty) and
/// <see cref="Label"/> is a fixed string chosen by code — never model-generated prose, and never a
/// claim that the prompt is definitely wrong.
/// </summary>
public sealed record FidelityConcern(
    FidelityConcernKind Kind,
    string Label,
    string SourceText,
    string? ClauseId = null,
    double? Probability = null,
    double? Confidence = null)
{
    /// <summary>Deterministic code checks carry no model probability; model findings do.</summary>
    public bool FromCode => Probability == null;
}

/// <summary>One numbered span of the original dictation. IDs are what the model answers about.</summary>
public sealed record TranscriptClause(string Id, string Text);

/// <summary>Everything the exact, model-free layer established about a rewrite.</summary>
public sealed record CodeFidelityFindings(
    IReadOnlyList<FidelityConcern> Concerns,
    IReadOnlyList<string> CheckedNumbers,
    IReadOnlyList<string> CheckedIdentifiers,
    int TranscriptProhibitions,
    int RewriteProhibitions)
{
    public bool Any => Concerns.Count > 0;
}

/// <summary>
/// The exact half of the fidelity check: everything that can be decided by string comparison is
/// decided here, for free and with no network call. Only the genuinely semantic residue
/// ("did this whole instruction survive in some other wording?") is worth a model call.
///
/// The existing <see cref="PromptRefinementService.IsSuspectedSummary"/> guard is the length-ratio
/// baseline and stays exactly as it is. This layer is additive: a long rewrite that keeps the word
/// count but silently changes <c>getUserById</c> to <c>getUserByID</c>, or drops "30 seconds",
/// passes the ratio guard and fails here.
/// </summary>
public static class PromptFidelityAnalyzer
{
    // ── Fixed user-facing labels ────────────────────────────────────────
    // Deliberately hedged for model findings ("may be") and factual for code findings (the token
    // really is absent). None of them assert the prompt is wrong — the user decides.

    public const string LabelMissingNumber = "A number you said is not in the prompt";
    public const string LabelMissingIdentifier = "A name you said is not in the prompt";
    public const string LabelDroppedProhibition = "You said what NOT to do; the prompt has no such instruction";
    public const string LabelOmittedClause = "This part of what you said may be missing from the prompt";
    public const string LabelContradictedClause = "The prompt may contradict this part of what you said";
    public const string LabelAddedRequirement = "The prompt may add a requirement you did not ask for";

    // Sentence-ish boundaries. The transcript has already been through TextPostProcessor, so
    // punctuation is normalized by the time we see it.
    private static readonly Regex ClauseSplit = new(@"(?<=[.!?…])\s+|\r?\n+", RegexOptions.Compiled);

    // Code-ish tokens: backtick spans, paths, dotted names, snake_case, camel/PascalCase, files
    // with a known extension. Deliberately NOT bare ALLCAPS words (API, SQL, CSS) — dropping one
    // of those is usually harmless and flagging them would manufacture false alarms.
    private static readonly Regex IdentifierPattern = new(
        """
        `[^`]{1,60}`
        | \b[\w.-]+[/\\][\w./\\-]+
        | \b[A-Za-z_][\w-]*\.(?:cs|ts|tsx|js|jsx|mjs|py|json|md|ya?ml|xaml|html|css|scss|sql|sh|ps1|rs|go|java|rb|php|txt|toml|ini|cfg|xml|svelte|vue)\b
        | \b[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+\b
        | \b[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]+\b
        | \b[a-z][a-z0-9]*[A-Z][A-Za-z0-9]*\b
        | \b[A-Z][a-z0-9]+[A-Z][A-Za-z0-9]*\b
        """,
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex NumberPattern = new(@"\d+(?:[.,]\d+)*", RegexOptions.Compiled);

    // Explicit prohibitions, English and Croatian. Matched as substrings of the lower-cased text so
    // Croatian inflection ("ne smije" / "ne smiješ" / "ne smijemo") is covered by a stem.
    private static readonly string[] ProhibitionMarkers =
    {
        // English
        "do not", "don't", "dont ", "never ", "must not", "mustn't", "should not", "shouldn't",
        "cannot ", "can't ", "under no circumstances", "avoid ", "no longer",
        // Croatian
        "nemoj", "ne smij", "nikako", "nikad", "ne diraj", "ne mijenjaj", "ne radi",
        "ne treba", "zabranjeno", "bez da",
    };

    // Spoken self-correction. A value stated BEFORE one of these and restated after it was
    // superseded by the speaker, so its absence from the rewrite is correct, not a loss.
    private static readonly string[] CorrectionMarkers =
    {
        "no wait", "wait no", "wait,", "actually", "scratch that", "sorry", "make that",
        "i mean", "rather,", "no, make", "no, use", "no, set", "correction",
        "ne, nego", "nego ", "zapravo", "ustvari", "pardon", "oprosti", "ispravak", "ne, stavi",
    };

    /// <summary>
    /// Splits the dictation into numbered clauses. When there are more sentences than
    /// <paramref name="maxClauses"/>, adjacent sentences are merged so the whole transcript stays
    /// covered — dropping the tail would make "omitted" unanswerable for exactly the part we never
    /// showed the model.
    /// </summary>
    public static IReadOnlyList<TranscriptClause> ExtractClauses(string transcript, int maxClauses)
    {
        if (string.IsNullOrWhiteSpace(transcript) || maxClauses < 1)
            return Array.Empty<TranscriptClause>();

        var sentences = ClauseSplit.Split(transcript)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        if (sentences.Count == 0)
            return Array.Empty<TranscriptClause>();

        if (sentences.Count > maxClauses)
        {
            // Merge into maxClauses buckets, keeping original order and full coverage.
            var merged = new List<string>(maxClauses);
            var perBucket = (int)Math.Ceiling(sentences.Count / (double)maxClauses);
            for (int i = 0; i < sentences.Count; i += perBucket)
                merged.Add(string.Join(" ", sentences.Skip(i).Take(perBucket)));
            sentences = merged;
        }

        return sentences.Select((text, i) => new TranscriptClause($"c{i + 1}", text)).ToList();
    }

    /// <summary>
    /// Exact checks over the dictation and its rewrite. Returns only findings that are literally
    /// true (the token is absent / no prohibition survived), so a finding here never depends on a
    /// model being right.
    /// </summary>
    public static CodeFidelityFindings Compare(string transcript, string rewrite)
    {
        if (string.IsNullOrWhiteSpace(transcript) || string.IsNullOrWhiteSpace(rewrite))
            return new CodeFidelityFindings(
                Array.Empty<FidelityConcern>(), Array.Empty<string>(), Array.Empty<string>(), 0, 0);

        var clauses = ExtractClauses(transcript, int.MaxValue);
        var concerns = new List<FidelityConcern>();
        var checkedNumbers = new List<string>();
        var checkedIdentifiers = new List<string>();
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var clause in clauses)
        {
            var (identifiers, numbers) = ExtractTokens(clause.Text);

            foreach (var token in identifiers)
            {
                if (!checkedIdentifiers.Contains(token, StringComparer.Ordinal))
                    checkedIdentifiers.Add(token);
                if (ContainsToken(rewrite, token, ignoreCase: false)) continue;
                if (!reported.Add("i:" + token)) continue;
                concerns.Add(new FidelityConcern(
                    FidelityConcernKind.MissingIdentifier, LabelMissingIdentifier, token, clause.Id));
            }

            foreach (var token in numbers)
            {
                if (!checkedNumbers.Contains(token, StringComparer.Ordinal))
                    checkedNumbers.Add(token);
                if (ContainsNumber(rewrite, token)) continue;
                if (!reported.Add("n:" + token)) continue;
                concerns.Add(new FidelityConcern(
                    FidelityConcernKind.MissingNumber, LabelMissingNumber, token, clause.Id));
            }
        }

        var transcriptProhibitions = CountProhibitions(transcript);
        var rewriteProhibitions = CountProhibitions(rewrite);

        // Only the total-loss case is reported. A rewrite that consolidates three prohibitions into
        // two is normal editing; a rewrite with NONE left after the speaker gave one is a real,
        // checkable loss. Partial loss is exactly what the model layer is for.
        if (transcriptProhibitions > 0 && rewriteProhibitions == 0)
        {
            var source = clauses.FirstOrDefault(c => CountProhibitions(c.Text) > 0);
            concerns.Add(new FidelityConcern(
                FidelityConcernKind.DroppedProhibition,
                LabelDroppedProhibition,
                source?.Text ?? transcript,
                source?.Id));
        }

        return new CodeFidelityFindings(
            concerns, checkedNumbers, checkedIdentifiers, transcriptProhibitions, rewriteProhibitions);
    }

    /// <summary>
    /// Pulls the checkable identifiers and numbers out of ONE clause, dropping values the speaker
    /// audibly superseded ("set the timeout to 30, actually make it 60" only commits to 60).
    /// Identifiers are matched first and masked out so their embedded digits are not re-reported
    /// as loose numbers.
    /// </summary>
    internal static (IReadOnlyList<string> identifiers, IReadOnlyList<string> numbers) ExtractTokens(string clause)
    {
        var correctionAt = LastCorrectionIndex(clause);

        var identifiers = new List<(string token, int index)>();
        var masked = new System.Text.StringBuilder(clause);

        foreach (Match m in IdentifierPattern.Matches(clause))
        {
            // The path alternative is greedy over dots, so it swallows a sentence-ending period:
            // "api/v2/pricing." must not become a token nobody could ever match.
            var token = m.Value.Trim('`').TrimEnd('.', ',', ':', ';', '!', '?', ')');
            if (token.Length < 2) continue;
            identifiers.Add((token, m.Index));
            for (int i = m.Index; i < m.Index + m.Length; i++) masked[i] = ' ';
        }

        var maskedText = masked.ToString();
        var numbers = NumberPattern.Matches(maskedText)
            .Where(m => !IsWordEmbedded(maskedText, m.Index))
            .Select(m => (token: m.Value, index: m.Index))
            .ToList();

        return (Survivors(identifiers, correctionAt), Survivors(numbers, correctionAt));
    }

    /// <summary>
    /// Keeps only the values that survive the speaker's last self-correction: if the same kind of
    /// value appears after the correction marker, everything of that kind before it was abandoned.
    /// </summary>
    private static IReadOnlyList<string> Survivors(List<(string token, int index)> tokens, int correctionAt)
    {
        if (correctionAt < 0 || tokens.Count == 0)
            return tokens.Select(t => t.token).Distinct(StringComparer.Ordinal).ToList();

        var after = tokens.Where(t => t.index > correctionAt).Select(t => t.token).ToList();
        if (after.Count == 0)
            return tokens.Select(t => t.token).Distinct(StringComparer.Ordinal).ToList();

        return after.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// True when a digit run is glued to the end of a word rather than being a value the speaker
    /// stated: the "2" in <c>v2</c>, the "1" in <c>h1</c>, the "3" in <c>mp3</c>. Those travel with
    /// the word they belong to, so checking them separately only manufactures false alarms.
    /// </summary>
    private static bool IsWordEmbedded(string text, int index) =>
        index > 0 && (char.IsLetter(text[index - 1]) || text[index - 1] == '_');

    private static int LastCorrectionIndex(string clause)
    {
        var lower = clause.ToLowerInvariant();
        int best = -1;
        foreach (var marker in CorrectionMarkers)
        {
            var at = lower.LastIndexOf(marker, StringComparison.Ordinal);
            if (at > best) best = at;
        }
        return best;
    }

    /// <summary>Counts explicit prohibition markers (English + Croatian) in a text.</summary>
    internal static int CountProhibitions(string text)
    {
        var lower = " " + text.ToLowerInvariant() + " ";
        int count = 0;
        foreach (var marker in ProhibitionMarkers)
        {
            int at = 0;
            while ((at = lower.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += marker.Length;
            }
        }
        return count;
    }

    /// <summary>
    /// Whole-token containment. <c>30</c> must not match inside <c>300</c>, and
    /// <c>getUserById</c> must not be satisfied by <c>getUserByIdentifier</c>.
    /// </summary>
    internal static bool ContainsToken(string haystack, string token, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(token)) return true;
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int from = 0;
        while ((from = haystack.IndexOf(token, from, comparison)) >= 0)
        {
            var beforeOk = from == 0 || !IsTokenChar(haystack[from - 1]);
            var end = from + token.Length;
            var afterOk = end >= haystack.Length || !IsTokenChar(haystack[end]);
            if (beforeOk && afterOk) return true;
            from = end;
        }
        return false;
    }

    /// <summary>
    /// Numeric containment with unit tolerance: "30 seconds" is preserved by "30s", "30 ms" and
    /// "30-second", but NOT by "300", "1.30" or "v30". Letters may follow a number (a unit); digits
    /// and decimal separators may not, on either side.
    /// </summary>
    internal static bool ContainsNumber(string haystack, string number)
    {
        if (string.IsNullOrEmpty(number)) return true;
        int from = 0;
        while ((from = haystack.IndexOf(number, from, StringComparison.Ordinal)) >= 0)
        {
            var end = from + number.Length;
            var before = from == 0 ? ' ' : haystack[from - 1];
            var beforeOk = !char.IsLetterOrDigit(before) && before != '_'
                           && !(IsDecimalSeparator(before) && from >= 2 && char.IsDigit(haystack[from - 2]));

            var after = end >= haystack.Length ? ' ' : haystack[end];
            var afterOk = !char.IsDigit(after)
                          && !(IsDecimalSeparator(after) && end + 1 < haystack.Length && char.IsDigit(haystack[end + 1]));

            if (beforeOk && afterOk) return true;
            from = end;
        }
        return false;
    }

    private static bool IsDecimalSeparator(char c) => c == '.' || c == ',';

    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
