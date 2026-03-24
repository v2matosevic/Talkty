using System.Text.RegularExpressions;

namespace Talkty.App.Services;

/// <summary>
/// Post-processes Whisper transcription output:
/// 1. Smart segment joining — merges fragments split by pauses, preserves real sentence breaks
/// 2. Vocabulary replacements — case-insensitive word-boundary matching (cloud→Claude)
/// 3. Hallucination stripping — removes Whisper artifacts at audio boundaries
/// 4. Punctuation cleanup — fixes spacing, double periods, trailing artifacts
/// </summary>
public static partial class TextPostProcessor
{
    [GeneratedRegex(
        @"(?:\s*(?:Thanks? (?:you |for (?:watching|listening))?\.?|" +
        @"Bye\.?|" +
        @"See you\.?|" +
        @"Subscribe\.?|" +
        @"Please (?:subscribe|like)\.?|" +
        @"\[(?:MUSIC|BLANK_AUDIO|SILENCE|APPLAUSE)\]|" +
        @"\((?:music|silence|applause|laughing|laughter)\)|" +
        @"♪+|" +
        @"\.{3,}|" +
        @"you\.?\s*$))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HallucinationPattern();

    // Matches a sentence-ending period followed by a lowercase word — indicates a false sentence break.
    // "I want to build. a new feature" → the period before "a" is a false break from a pause.
    [GeneratedRegex(@"\.(\s+)([a-z])", RegexOptions.Compiled)]
    private static partial Regex FalseSentenceBreak();

    // Matches leading/trailing ellipsis that Whisper adds to continued segments.
    [GeneratedRegex(@"^\s*\.{2,}\s*|\s*\.{2,}\s*$", RegexOptions.Compiled)]
    private static partial Regex SegmentEllipsis();

    // Matches multiple spaces.
    [GeneratedRegex(@"  +", RegexOptions.Compiled)]
    private static partial Regex MultipleSpaces();

    // Matches double/triple periods that aren't ellipsis.
    [GeneratedRegex(@"\.{2}(?!\.)", RegexOptions.Compiled)]
    private static partial Regex DoublePeriod();

    /// <summary>
    /// Joins Whisper segments intelligently. Whisper adds terminal punctuation to each segment,
    /// which creates false sentence breaks when the user paused mid-thought.
    /// This method merges fragments while preserving intentional sentence boundaries.
    /// </summary>
    public static string JoinSegments(IList<string> segments)
    {
        if (segments.Count == 0) return "";
        if (segments.Count == 1) return segments[0].Trim();

        var result = new System.Text.StringBuilder();

        for (int i = 0; i < segments.Count; i++)
        {
            // Strip leading/trailing ellipsis Whisper adds to continued segments
            // e.g., "So what is the best..." + "...cold water" → "So what is the best" + "cold water"
            var segment = SegmentEllipsis().Replace(segments[i], "").Trim();
            if (string.IsNullOrEmpty(segment)) continue;

            if (result.Length == 0)
            {
                result.Append(segment);
                continue;
            }

            var lastChar = result[^1];
            var firstChar = segment.Length > 0 ? segment[0] : ' ';

            // If the previous segment ended with sentence-ending punctuation (.!?)
            // and this segment starts with uppercase → real sentence break, keep it
            if (IsSentenceEnd(lastChar) && char.IsUpper(firstChar))
            {
                result.Append(' ');
                result.Append(segment);
            }
            // If previous ended with .!? but this starts lowercase → false break from pause
            // Remove the terminal punctuation and merge as one sentence
            else if (IsSentenceEnd(lastChar) && char.IsLower(firstChar))
            {
                // Remove the trailing punctuation (it was a false sentence break)
                result.Length--;
                // Also remove trailing space if present
                while (result.Length > 0 && result[^1] == ' ')
                    result.Length--;

                result.Append(' ');
                result.Append(segment);
                Log.Debug($"Merged fragment: ...{segment[..Math.Min(30, segment.Length)]}");
            }
            // If previous didn't end with punctuation, just join with a space
            else
            {
                if (lastChar != ' ') result.Append(' ');
                result.Append(segment);
            }
        }

        return result.ToString().Trim();
    }

    private static bool IsSentenceEnd(char c) => c is '.' or '!' or '?';

    /// <summary>
    /// Cleans up punctuation artifacts in the final text:
    /// - Merges remaining false sentence breaks (period + lowercase)
    /// - Removes double periods
    /// - Normalizes spacing
    /// </summary>
    public static string CleanupPunctuation(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var result = text;

        // Merge false sentence breaks: "build. a feature" → "build a feature"
        result = FalseSentenceBreak().Replace(result, " $2");

        // Remove double periods (but not ellipsis "...")
        result = DoublePeriod().Replace(result, ".");

        // Normalize multiple spaces to single
        result = MultipleSpaces().Replace(result, " ");

        // Ensure the text ends with proper punctuation
        result = result.Trim();
        if (result.Length > 0 && !IsSentenceEnd(result[^1]) && result[^1] != ',')
        {
            result += ".";
        }

        if (result != text.Trim())
        {
            Log.Debug($"Punctuation cleanup applied");
        }

        return result;
    }

    /// <summary>
    /// Applies vocabulary replacements to the transcribed text.
    /// Matches are case-insensitive and word-boundary-aware to avoid partial matches.
    /// </summary>
    public static string ApplyReplacements(string text, IReadOnlyDictionary<string, string> replacements)
    {
        if (string.IsNullOrWhiteSpace(text) || replacements.Count == 0)
            return text;

        var result = text;

        foreach (var (pattern, replacement) in replacements)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            var regex = new Regex(
                @"\b" + Regex.Escape(pattern) + @"\b",
                RegexOptions.IgnoreCase);

            result = regex.Replace(result, match =>
            {
                if (match.Value.Length > 0 && char.IsUpper(match.Value[0]) &&
                    replacement.Length > 0 && char.IsLower(replacement[0]))
                {
                    return char.ToUpper(replacement[0]) + replacement[1..];
                }
                return replacement;
            });
        }

        if (result != text)
        {
            Log.Debug($"Text replacements applied: {text.Length} → {result.Length} chars");
        }

        return result;
    }

    /// <summary>
    /// Strips common Whisper hallucinations that appear at audio boundaries.
    /// </summary>
    public static string StripHallucinations(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var cleaned = HallucinationPattern().Replace(text, "").Trim();

        if (cleaned != text.Trim())
        {
            Log.Debug($"Hallucination stripped: \"{text.Trim()}\" → \"{cleaned}\"");
        }

        return cleaned;
    }
}
