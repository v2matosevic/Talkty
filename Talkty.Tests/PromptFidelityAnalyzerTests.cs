using Talkty.App.Services;
using Xunit;

namespace Talkty.Tests;

/// <summary>
/// The exact, model-free half of the fidelity check. Every finding this layer produces must be
/// literally true, so these tests care as much about what it stays SILENT about as what it catches.
/// </summary>
public class PromptFidelityAnalyzerTests
{
    // ── Clause numbering ────────────────────────────────────────────────

    [Fact]
    public void Clauses_AreNumberedInOrder()
    {
        var clauses = PromptFidelityAnalyzer.ExtractClauses(
            "Fix the login bug. Add a test for it. Do not touch the migrations.", 12);

        Assert.Equal(3, clauses.Count);
        Assert.Equal(new[] { "c1", "c2", "c3" }, clauses.Select(c => c.Id));
        Assert.Equal("Add a test for it.", clauses[1].Text);
    }

    [Fact]
    public void Clauses_MergeRatherThanTruncateWhenOverTheCap()
    {
        var transcript = string.Join(" ", Enumerable.Range(1, 9).Select(i => $"Requirement number {i}."));
        var clauses = PromptFidelityAnalyzer.ExtractClauses(transcript, 3);

        Assert.Equal(3, clauses.Count);
        // Dropping the tail would make "omitted" unanswerable for text we never showed the model,
        // so every sentence must still appear somewhere.
        for (int i = 1; i <= 9; i++)
            Assert.Contains(clauses, c => c.Text.Contains($"number {i}."));
    }

    // ── Token extraction ────────────────────────────────────────────────

    [Fact]
    public void Tokens_PickUpPathsFilesDottedAndCamelCaseNames()
    {
        var (identifiers, _) = PromptFidelityAnalyzer.ExtractTokens(
            "In src/lib/users.ts change getUserById and OrderTable.tsx and the Talkty.App namespace and user_id.");

        Assert.Contains("src/lib/users.ts", identifiers);
        Assert.Contains("getUserById", identifiers);
        Assert.Contains("OrderTable.tsx", identifiers);
        Assert.Contains("Talkty.App", identifiers);
        Assert.Contains("user_id", identifiers);
    }

    [Fact]
    public void Tokens_IgnoreBareAcronymsToAvoidManufacturedAlarms()
    {
        // Dropping a redundant mention of "API" or "SQL" is harmless; flagging it would be noise.
        var (identifiers, _) = PromptFidelityAnalyzer.ExtractTokens("Call the API and run the SQL.");
        Assert.DoesNotContain("API", identifiers);
        Assert.DoesNotContain("SQL", identifiers);
    }

    [Fact]
    public void Tokens_DoNotReportDigitsThatBelongToAnIdentifier()
    {
        var (identifiers, numbers) = PromptFidelityAnalyzer.ExtractTokens("Add the endpoint api/v2/pricing.");
        Assert.Contains("api/v2/pricing", identifiers);
        Assert.DoesNotContain("2", numbers);
    }

    [Fact]
    public void Tokens_DropValuesTheSpeakerSuperseded()
    {
        var (_, numbers) = PromptFidelityAnalyzer.ExtractTokens(
            "Set the cache TTL to 300 seconds, no wait, actually make it 600.");

        Assert.Contains("600", numbers);
        Assert.DoesNotContain("300", numbers);
    }

    // ── Whole-token matching ────────────────────────────────────────────

    [Fact]
    public void Identifier_MatchIsExactAndCaseSensitive()
    {
        Assert.True(PromptFidelityAnalyzer.ContainsToken("call getUserById(id)", "getUserById", false));
        // A one-character change is the whole point of this layer.
        Assert.False(PromptFidelityAnalyzer.ContainsToken("call getUserByID(id)", "getUserById", false));
        // A longer name is not the same name.
        Assert.False(PromptFidelityAnalyzer.ContainsToken("call getUserByIdentifier(id)", "getUserById", false));
    }

    [Fact]
    public void Number_MatchToleratesUnitsButNotOtherNumbers()
    {
        Assert.True(PromptFidelityAnalyzer.ContainsNumber("timeout 30s", "30"));
        Assert.True(PromptFidelityAnalyzer.ContainsNumber("wait 30 ms", "30"));
        Assert.True(PromptFidelityAnalyzer.ContainsNumber("a 30-second timeout", "30"));
        Assert.False(PromptFidelityAnalyzer.ContainsNumber("timeout 300s", "30"));
        Assert.False(PromptFidelityAnalyzer.ContainsNumber("version 1.30", "30"));
        Assert.False(PromptFidelityAnalyzer.ContainsNumber("set it to v30", "30"));
    }

    // ── Prohibitions, English and Croatian ──────────────────────────────

    [Theory]
    [InlineData("Do not deploy this.", 1)]
    [InlineData("Don't deploy and never restart it.", 2)]
    [InlineData("Nemoj dirati migracije.", 2)]  // "nemoj" and "ne diraj"-stem free text both count
    [InlineData("Add a spinner to the button.", 0)]
    public void Prohibitions_AreCountedInBothLanguages(string text, int atLeast)
    {
        var count = PromptFidelityAnalyzer.CountProhibitions(text);
        if (atLeast == 0) Assert.Equal(0, count);
        else Assert.True(count >= 1, $"expected a prohibition in: {text}");
    }

    [Fact]
    public void TotalProhibitionLossIsReported()
    {
        var findings = PromptFidelityAnalyzer.Compare(
            "Refactor the handler. Do not deploy it to production.",
            "Refactor the handler so the verification is testable.");

        Assert.Contains(findings.Concerns, c => c.Kind == FidelityConcernKind.DroppedProhibition);
        Assert.Equal(1, findings.TranscriptProhibitions);
        Assert.Equal(0, findings.RewriteProhibitions);
    }

    [Fact]
    public void PartialProhibitionLossIsLeftToTheModelLayer()
    {
        // Two prohibitions in, one out. Deliberately NOT reported here: a count check cannot tell a
        // consolidation from a loss, and a false alarm on faithful editing is the worse failure.
        var findings = PromptFidelityAnalyzer.Compare(
            "Do not add a dependency. And do not touch the numbering logic.",
            "Do not add a dependency; reuse the existing queue.");

        Assert.DoesNotContain(findings.Concerns, c => c.Kind == FidelityConcernKind.DroppedProhibition);
    }

    // ── Silence on faithful rewrites ────────────────────────────────────

    [Fact]
    public void FaithfulRewriteProducesNoFindings()
    {
        var findings = PromptFidelityAnalyzer.Compare(
            "In src/lib/users.ts make getUserById throw a NotFoundError after 30 seconds.",
            "**Task**\nIn src/lib/users.ts, make getUserById throw a NotFoundError after a 30 second wait.");

        Assert.Empty(findings.Concerns);
    }

    [Fact]
    public void EmptyInputsAreNotFindings()
    {
        Assert.Empty(PromptFidelityAnalyzer.Compare("", "something").Concerns);
        Assert.Empty(PromptFidelityAnalyzer.Compare("something", "").Concerns);
    }

    [Fact]
    public void ReportedSourceTextIsTheUsersOwnWords()
    {
        var findings = PromptFidelityAnalyzer.Compare(
            "Rename getUserById to loadUser.",
            "Rename getUserByID to loadUser.");

        var concern = Assert.Single(findings.Concerns);
        // The label is a fixed code-chosen string and the quoted text is the dictated token —
        // never model prose, and never a claim that the prompt is definitely wrong.
        Assert.Equal(PromptFidelityAnalyzer.LabelMissingIdentifier, concern.Label);
        Assert.Equal("getUserById", concern.SourceText);
        Assert.True(concern.FromCode);
    }
}
