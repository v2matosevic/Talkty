using Talkty.App.Services;
using Xunit;

namespace Talkty.Tests;

/// <summary>
/// Locks in the prompt-refinement completeness guard (PromptRefinementService.IsSuspectedSummary).
/// The guard catches a model that SUMMARIZED a substantial dictation instead of expanding it, so the
/// chain can escalate to a stronger model. Thresholds are tuned from real logs — see the cases below.
/// </summary>
public class PromptCompletenessGuardTests
{
    // Helper: build a string of a given length (content is irrelevant — the guard is length-only).
    private static string Chars(int n) => new('x', n);

    // ── Real-log regression: the case that motivated the guard ───────────

    [Fact]
    public void Guard_CatchesMiniMaxSummarization_578to195()
    {
        // 02:56:35 in the logs: minimax/minimax-m3 returned 195 chars from a 578-char dictation (0.34).
        var input = Chars(578);
        var output = Chars(195);
        Assert.True(PromptRefinementService.IsSuspectedSummary(input, output));
    }

    [Fact]
    public void Guard_PassesGeminiExpansion_1038to2423()
    {
        // 02:58:38: gemini-3.5-flash expanded 1038 → 2423 chars — exactly what Prompting should do.
        var input = Chars(1038);
        var output = Chars(2423);
        Assert.False(PromptRefinementService.IsSuspectedSummary(input, output));
    }

    // ── Short inputs are never guarded (they legitimately produce short prompts) ──

    [Fact]
    public void Guard_IgnoresShortInput_EvenIfOutputShrinks()
    {
        // 88-char "What about MiniMax M3?..." — below the 400-char substantial threshold.
        var input = Chars(88);
        var output = Chars(20);
        Assert.False(PromptRefinementService.IsSuspectedSummary(input, output));
    }

    [Fact]
    public void Guard_IgnoresInputJustBelowThreshold()
    {
        var input = Chars(399);
        var output = Chars(10);
        Assert.False(PromptRefinementService.IsSuspectedSummary(input, output));
    }

    // ── Ratio boundary around 0.6 for a substantial input ───────────────

    [Fact]
    public void Guard_CatchesOutputBelowRatio()
    {
        // 500 * 0.6 = 300; an output of 250 is below the floor → summarized.
        var input = Chars(500);
        var output = Chars(250);
        Assert.True(PromptRefinementService.IsSuspectedSummary(input, output));
    }

    [Fact]
    public void Guard_PassesOutputAtFillerTrimLevel()
    {
        // 500 → 400 (0.8) is normal filler/repetition trimming, not summarization → keep.
        var input = Chars(500);
        var output = Chars(400);
        Assert.False(PromptRefinementService.IsSuspectedSummary(input, output));
    }

    [Fact]
    public void Guard_TrimsWhitespaceBeforeMeasuring()
    {
        // Padding whitespace must not let a short summary sneak past the ratio check.
        var input = Chars(500);
        var output = Chars(250) + new string(' ', 200);
        Assert.True(PromptRefinementService.IsSuspectedSummary(input, output));
    }
}
