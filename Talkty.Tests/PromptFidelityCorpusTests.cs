using Talkty.App.Services;
using Xunit;
using Xunit.Abstractions;

namespace Talkty.Tests;

/// <summary>
/// Runs the labeled corpus through the exact layer and through the EXISTING length-ratio baseline
/// (<see cref="PromptRefinementService.IsSuspectedSummary"/>), so the comparison the pilot is
/// supposed to produce is a test result rather than a claim in a document.
///
/// These tests do not call Jev. The model layer is measured separately by the paid live
/// qualification (tools/jev-fidelity-check.ps1), whose numbers go in docs/PROMPT-FIDELITY.md.
/// </summary>
public class PromptFidelityCorpusTests
{
    private readonly ITestOutputHelper _output;
    public PromptFidelityCorpusTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Corpus_CoversEveryScenarioTheBriefAsksFor()
    {
        var cases = FidelityCorpus.Cases;

        Assert.Contains(cases, c => c.Scenario.StartsWith("omitted-prohibition"));
        Assert.Contains(cases, c => c.Scenario == "altered-identifier");
        Assert.Contains(cases, c => c.Scenario.StartsWith("changed-value"));
        Assert.Contains(cases, c => c.Scenario == "self-correction");
        Assert.Contains(cases, c => c.Scenario.StartsWith("faithful"));
        Assert.Contains(cases, c => c.Scenario == "filler-removal");
        Assert.Contains(cases, c => c.Scenario.StartsWith("multiple-requirements"));
        Assert.Contains(cases, c => c.Scenario.StartsWith("contradiction"));
        Assert.Contains(cases, c => c.Scenario == "added-requirement");

        // Both languages, and a mixed-language case.
        Assert.Contains(cases, c => c.Language == "en");
        Assert.Contains(cases, c => c.Language == "hr");
        Assert.Contains(cases, c => c.Scenario.StartsWith("mixed-language"));

        // A held-out split exists and is not trivially small.
        Assert.True(cases.Count(c => c.IsDevelopment) >= 10);
        Assert.True(cases.Count(c => c.IsHoldout) >= 8);

        // Both outcomes are represented, so "always alarm" and "never alarm" both fail.
        Assert.True(cases.Count(c => c.ExpectConcern) >= 8);
        Assert.True(cases.Count(c => !c.ExpectConcern) >= 8);
    }

    [Fact]
    public void ExactLayer_CatchesEveryCaseItIsResponsibleFor()
    {
        foreach (var c in FidelityCorpus.Cases.Where(c => c.CodeShouldCatch))
        {
            var findings = PromptFidelityAnalyzer.Compare(c.Transcript, c.Rewrite);
            Assert.True(findings.Any, $"{c.Id} ({c.Scenario}): exact layer found nothing. {c.Label}");

            foreach (var kind in c.ExpectedKinds.Where(IsCodeKind))
                Assert.Contains(findings.Concerns, f => f.Kind.ToString() == kind);
        }
    }

    [Fact]
    public void ExactLayer_StaysSilentOnEveryFaithfulRewrite()
    {
        foreach (var c in FidelityCorpus.Cases.Where(c => !c.ExpectConcern))
        {
            var findings = PromptFidelityAnalyzer.Compare(c.Transcript, c.Rewrite);
            Assert.False(findings.Any,
                $"{c.Id} ({c.Scenario}): false alarm — " +
                string.Join(", ", findings.Concerns.Select(f => $"{f.Kind}('{f.SourceText}')")) +
                $". {c.Label}");
        }
    }

    [Fact]
    public void ExactLayer_AttributesFindingsToTheRightClause()
    {
        foreach (var c in FidelityCorpus.Cases.Where(c => c.CodeShouldCatch && c.ExpectedClause != null))
        {
            var findings = PromptFidelityAnalyzer.Compare(c.Transcript, c.Rewrite);
            var codeKinds = c.ExpectedKinds.Where(IsCodeKind).ToList();
            if (codeKinds.Count == 0) continue;

            Assert.Contains(findings.Concerns,
                f => codeKinds.Contains(f.Kind.ToString()) && f.ClauseId == c.ExpectedClause);
        }
    }

    /// <summary>
    /// The comparison the pilot exists to make. The existing guard measures LENGTH, so a fluent
    /// rewrite that keeps the word count while losing one requirement passes it. This test records
    /// the actual counts and asserts the relationship the exact layer is supposed to improve:
    /// it must catch strictly more loss cases than the length guard, while raising no false alarm.
    /// </summary>
    [Fact]
    public void ExactLayer_BeatsTheLengthGuardWithoutAddingFalseAlarms()
    {
        int lossCases = 0, guardCaught = 0, codeCaught = 0;
        int faithfulCases = 0, guardFalse = 0, codeFalse = 0;

        _output.WriteLine($"{"case",-10} {"split",-12} {"lang",-5} {"expect",-7} {"guard",-6} {"code",-5} scenario");
        foreach (var c in FidelityCorpus.Cases)
        {
            var guard = PromptRefinementService.IsSuspectedSummary(c.Transcript, c.Rewrite);
            var code = PromptFidelityAnalyzer.Compare(c.Transcript, c.Rewrite).Any;

            if (c.ExpectConcern)
            {
                lossCases++;
                if (guard) guardCaught++;
                if (code) codeCaught++;
            }
            else
            {
                faithfulCases++;
                if (guard) guardFalse++;
                if (code) codeFalse++;
            }

            _output.WriteLine($"{c.Id,-10} {c.Split,-12} {c.Language,-5} " +
                              $"{(c.ExpectConcern ? "loss" : "faithful"),-7} {guard,-6} {code,-5} {c.Scenario}");
        }

        _output.WriteLine("");
        _output.WriteLine($"fidelity-loss cases: {lossCases} | length guard caught {guardCaught} | exact layer caught {codeCaught}");
        _output.WriteLine($"faithful cases: {faithfulCases} | length guard false alarms {guardFalse} | exact layer false alarms {codeFalse}");
        _output.WriteLine("The remaining loss cases are the model layer's job; see docs/PROMPT-FIDELITY.md.");

        Assert.Equal(0, codeFalse);
        Assert.True(codeCaught > guardCaught,
            $"exact layer caught {codeCaught} of {lossCases}, length guard caught {guardCaught}");
    }

    /// <summary>
    /// The honest limit: several corpus cases are invisible to any string comparison. This test
    /// pins that down so nobody later mistakes the exact layer for a complete check.
    /// </summary>
    [Fact]
    public void SomeLossesAreOnlyReachableByTheModelLayer()
    {
        var modelOnly = FidelityCorpus.Cases
            .Where(c => c.ExpectConcern && !c.CodeShouldCatch)
            .ToList();

        Assert.NotEmpty(modelOnly);
        foreach (var c in modelOnly)
        {
            Assert.False(PromptFidelityAnalyzer.Compare(c.Transcript, c.Rewrite).Any,
                $"{c.Id} was labeled model-only but the exact layer caught it — re-label the case.");
            _output.WriteLine($"{c.Id} ({c.Scenario}, {c.Language}): {c.Label}");
        }
    }

    private static bool IsCodeKind(string kind) =>
        kind is nameof(FidelityConcernKind.MissingIdentifier)
             or nameof(FidelityConcernKind.MissingNumber)
             or nameof(FidelityConcernKind.DroppedProhibition);
}
