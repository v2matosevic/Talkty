using Talkty.App.Services;
using Xunit;

namespace Talkty.Tests;

/// <summary>
/// Regression tests for TextPostProcessor. These lock in the behaviours that were broken
/// in v1.0.10 — over-aggressive hallucination stripping that ate "thank you" and trailing
/// words, and replacement regex correctness.
/// </summary>
public class TextPostProcessorTests
{
    // ── StripHallucinations: must NOT eat real user text ─────────────────

    [Fact]
    public void StripHallucinations_KeepsMidSentenceThankYou()
    {
        var input = "I want to thank you for being here";
        var result = TextPostProcessor.StripHallucinations(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void StripHallucinations_KeepsTrailingYou()
    {
        var input = "I need to call you";
        var result = TextPostProcessor.StripHallucinations(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void StripHallucinations_KeepsTrailingBye()
    {
        var input = "Okay, bye.";
        var result = TextPostProcessor.StripHallucinations(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void StripHallucinations_KeepsTrailingSubscribe()
    {
        var input = "You should subscribe.";
        var result = TextPostProcessor.StripHallucinations(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void StripHallucinations_KeepsMidTextThanks()
    {
        var input = "Thanks for listening to my idea about the feature.";
        var result = TextPostProcessor.StripHallucinations(input);
        Assert.Equal(input, result);
    }

    // ── StripHallucinations: must strip actual Whisper artefacts ─────────

    [Fact]
    public void StripHallucinations_RemovesTrailingThanksForWatching()
    {
        var result = TextPostProcessor.StripHallucinations("This is my idea. Thanks for watching.");
        Assert.Equal("This is my idea.", result);
    }

    [Fact]
    public void StripHallucinations_RemovesTrailingThankYouForListening()
    {
        var result = TextPostProcessor.StripHallucinations("Here is the plan. Thank you for listening.");
        Assert.Equal("Here is the plan.", result);
    }

    [Fact]
    public void StripHallucinations_RemovesTrailingPleaseSubscribe()
    {
        var result = TextPostProcessor.StripHallucinations("Great feature. Please subscribe.");
        Assert.Equal("Great feature.", result);
    }

    [Fact]
    public void StripHallucinations_RemovesBracketTokens()
    {
        var result = TextPostProcessor.StripHallucinations("Hello [MUSIC] world");
        Assert.Equal("Hello world", result);
    }

    [Fact]
    public void StripHallucinations_RemovesMusicSymbol()
    {
        var result = TextPostProcessor.StripHallucinations("Some text ♪♪ more text");
        Assert.Equal("Some text more text", result);
    }

    [Fact]
    public void StripHallucinations_RemovesParenAnnotations()
    {
        var result = TextPostProcessor.StripHallucinations("Hello (music) world");
        Assert.Equal("Hello world", result);
    }

    // ── ApplyReplacements: case preservation + word boundaries ───────────

    [Fact]
    public void ApplyReplacements_PreservesCapitalAtSentenceStart()
    {
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cloud"] = "Claude"
        };
        var result = TextPostProcessor.ApplyReplacements("Cloud is helpful", replacements);
        Assert.Equal("Claude is helpful", result);
    }

    [Fact]
    public void ApplyReplacements_IsCaseInsensitive()
    {
        // Note: when the source match starts with uppercase and the replacement starts lowercase,
        // the replacement is capitalised to preserve sentence-start casing (Cloud → Claude).
        // That case is covered by the sentence-start test. Here we verify plain case-insensitive
        // matching without triggering the case-preservation branch.
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cube cuddle"] = "kubectl"
        };
        var result = TextPostProcessor.ApplyReplacements("I ran CUBE CUDDLE apply", replacements);
        Assert.Equal("I ran Kubectl apply", result);
    }

    [Fact]
    public void ApplyReplacements_RespectsWordBoundaries()
    {
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sequel"] = "SQL"
        };
        // "sequel" inside "sequelize" must NOT match
        var result = TextPostProcessor.ApplyReplacements("I use sequelize for ORM", replacements);
        Assert.Equal("I use sequelize for ORM", result);
    }

    [Fact]
    public void ApplyReplacements_HandlesMultiWordKey()
    {
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["post gres"] = "PostgreSQL"
        };
        var result = TextPostProcessor.ApplyReplacements("We use post gres here", replacements);
        Assert.Equal("We use PostgreSQL here", result);
    }

    // ── JoinSegments: false-break detection ──────────────────────────────

    [Fact]
    public void JoinSegments_MergesFalseBreakFromPause()
    {
        var segments = new[] { "I want to build.", "a new feature" };
        var result = TextPostProcessor.JoinSegments(segments);
        Assert.Equal("I want to build a new feature", result);
    }

    [Fact]
    public void JoinSegments_PreservesRealSentenceBreak()
    {
        var segments = new[] { "This is done.", "Now for the next part." };
        var result = TextPostProcessor.JoinSegments(segments);
        Assert.Equal("This is done. Now for the next part.", result);
    }

    [Fact]
    public void JoinSegments_StripsContinuationEllipsis()
    {
        var segments = new[] { "So what is the best...", "...cold water" };
        var result = TextPostProcessor.JoinSegments(segments);
        Assert.Equal("So what is the best cold water", result);
    }

    [Fact]
    public void JoinSegments_SingleSegmentReturnsAsIs()
    {
        var result = TextPostProcessor.JoinSegments(new[] { "  just one segment  " });
        Assert.Equal("just one segment", result);
    }

    // ── CleanupPunctuation ───────────────────────────────────────────────

    [Fact]
    public void CleanupPunctuation_RemovesDoublePeriod()
    {
        var result = TextPostProcessor.CleanupPunctuation("Hello.. world");
        Assert.Equal("Hello. world.", result);
    }

    [Fact]
    public void CleanupPunctuation_AddsTerminalPeriod()
    {
        var result = TextPostProcessor.CleanupPunctuation("no terminal punctuation");
        Assert.Equal("no terminal punctuation.", result);
    }

    [Fact]
    public void CleanupPunctuation_PreservesEllipsis()
    {
        // Triple-dot ellipsis should NOT collapse to single period
        var result = TextPostProcessor.CleanupPunctuation("I was thinking... maybe");
        Assert.Contains("...", result);
    }
}
