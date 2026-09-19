using System.Text.Json;
using System.Text.Json.Nodes;
using Talkty.App;
using Talkty.App.Services;
using Xunit;

namespace Talkty.Tests;

/// <summary>
/// The decision that runs BEFORE refinement. Its one dangerous power is skipping the model call,
/// which changes what the user receives, so most of these tests are about how hard that is to earn.
/// Everything else must degrade to exactly today's behaviour.
/// </summary>
public class PromptClassifierTests
{
    private sealed class StubClient : IJevDecisionClient
    {
        public int Calls { get; private set; }
        public int? LastTimeoutMs { get; private set; }
        public Func<IReadOnlyList<JevQuestion>, CancellationToken, JevResult> Answer { get; set; } =
            (_, _) => JevResult.Fail(JevStatus.Unavailable, "timeout");

        public Task<JevResult> EvaluateAsync(string apiKey, JsonNode state,
            IReadOnlyList<JevQuestion> questions, CancellationToken cancellationToken = default,
            int? timeoutMs = null)
        {
            Calls++;
            LastTimeoutMs = timeoutMs;
            return Task.FromResult(Answer(questions, cancellationToken));
        }
    }

    private static JevEvaluation Evaluation(double needsStructure, string kind, double kindP,
        double kindConfidence, double complexity, double complexityConfidence)
    {
        var kindProbabilities = new Dictionary<string, double>
        {
            ["bug"] = 0, ["feature"] = 0, ["refactor"] = 0, ["question"] = 0, ["chore"] = 0
        };
        kindProbabilities[kind] = kindP;
        kindProbabilities[kind == "chore" ? "bug" : "chore"] = 1 - kindP;

        var levels = new Dictionary<string, double> { ["0"] = 0.34, ["1"] = 0.33, ["2"] = 0.33 };

        return new JevEvaluation("typesafe/jev-1.13-20260917", new Dictionary<string, JevAnswer>
        {
            [PromptClassifier.NeedsStructureId] =
                new(PromptClassifier.NeedsStructureId, null, null, null, needsStructure),
            [PromptClassifier.RequestKindId] =
                new(PromptClassifier.RequestKindId, kind, kindConfidence, kindProbabilities, null),
            [PromptClassifier.ComplexityId] =
                new(PromptClassifier.ComplexityId, null, complexityConfidence, levels, null, complexity),
        }, 420, 30, 0.00002, 480);
    }

    // ── The question set ────────────────────────────────────────────────

    [Fact]
    public void ThreeQuestionsRideOneRequestAndUseAllThreePrimitives()
    {
        var questions = PromptClassifier.BuildQuestions();

        Assert.Equal(3, questions.Count);
        Assert.Equal(JevQuestionKind.Noul, questions.Single(q => q.Id == PromptClassifier.NeedsStructureId).Kind);
        Assert.Equal(JevQuestionKind.Choice, questions.Single(q => q.Id == PromptClassifier.RequestKindId).Kind);
        Assert.Equal(JevQuestionKind.Score, questions.Single(q => q.Id == PromptClassifier.ComplexityId).Kind);

        var (json, failure) = JevDecisionClient.BuildRequest(
            new JsonObject { ["dictation"] = "rename the thing" }, questions);
        Assert.Null(failure);
        Assert.NotNull(json);
    }

    [Fact]
    public void OptionsSayWhatTheyDoNotCoverAndTreatTheDictationAsData()
    {
        var kind = PromptClassifier.BuildQuestions().Single(q => q.Id == PromptClassifier.RequestKindId);

        // Structured guidance is the point of the newly available object criteria: a boundary a
        // flat prose string could not express.
        var bug = Assert.IsType<JsonObject>(kind.Criteria!["bug"]);
        Assert.True(bug.ContainsKey("not_for"));

        Assert.All(PromptClassifier.BuildQuestions(),
            q => Assert.Contains("untrusted data", q.Instructions.ToJsonString()));
    }

    [Fact]
    public void TheClassifierUsesTheInPathDeadlineNotThePostDeliveryOne()
    {
        var client = new StubClient();
        var classifier = new PromptClassifier(client);
        classifier.SetApiKey("k");

        classifier.ClassifyAsync("do a thing").GetAwaiter().GetResult();

        // The speaker is waiting on this one, unlike the fidelity check.
        Assert.Equal(Constants.JevClassifierTimeoutMs, client.LastTimeoutMs);
        Assert.True(Constants.JevClassifierTimeoutMs < Constants.JevDecisionTimeoutMs);
    }

    // ── Failing safe ────────────────────────────────────────────────────

    [Theory]
    [InlineData(JevStatus.Unavailable, "timeout")]
    [InlineData(JevStatus.Invalid, "invalid_distribution")]
    [InlineData(JevStatus.Cancelled, "cancelled")]
    public async Task AnyFailureRefinesExactlyAsToday(JevStatus status, string code)
    {
        var client = new StubClient { Answer = (_, _) => JevResult.Fail(status, code) };
        var classifier = new PromptClassifier(client);
        classifier.SetApiKey("k");

        var plan = await classifier.ClassifyAsync("add a spinner");

        Assert.Equal(PromptPlanDecision.Refine, plan.Decision);
        Assert.Equal(code, plan.Reason);
        Assert.Equal(RequestKind.Unknown, plan.Kind);
    }

    [Fact]
    public async Task WithoutAKeyNothingIsSentAndTheFlowIsUnchanged()
    {
        var client = new StubClient();
        var classifier = new PromptClassifier(client);

        var plan = await classifier.ClassifyAsync("add a spinner");

        Assert.Equal(0, client.Calls);
        Assert.Equal(PromptPlanDecision.Refine, plan.Decision);
        Assert.Equal("no_api_key", plan.Reason);
    }

    [Fact]
    public async Task AThrowingTransportCannotCostTheUserTheirDictation()
    {
        var client = new StubClient { Answer = (_, _) => throw new InvalidOperationException("boom") };
        var classifier = new PromptClassifier(client);
        classifier.SetApiKey("k");

        var plan = await classifier.ClassifyAsync("add a spinner");

        Assert.Equal(PromptPlanDecision.Refine, plan.Decision);
        Assert.Equal("internal_error", plan.Reason);
    }

    // ── Skipping refinement: the only decision that changes the output ──

    [Fact]
    public void AConfidentlySimpleAskSkipsTheModelCall()
    {
        var plan = PromptClassifier.Interpret(Evaluation(
            needsStructure: 0.04, kind: "chore", kindP: 0.93, kindConfidence: 0.88,
            complexity: 0.1, complexityConfidence: 0.92));

        Assert.Equal(PromptPlanDecision.SkipRefinement, plan.Decision);
        Assert.Equal(RequestKind.Chore, plan.Kind);
        Assert.Equal("already_a_prompt", plan.Reason);
    }

    [Fact]
    public void ShortButMultiPartStillGetsRefined()
    {
        // "Change the session length to 30 days, add a remember-me box, and don't kill sessions on
        // other devices" is one breath and three requirements. Length is exactly the signal that
        // fails here, which is why the rubric and not a character count makes this call.
        var plan = PromptClassifier.Interpret(Evaluation(
            needsStructure: 0.88, kind: "feature", kindP: 0.8, kindConfidence: 0.75,
            complexity: 1.9, complexityConfidence: 0.9));

        Assert.Equal(PromptPlanDecision.Refine, plan.Decision);
    }

    [Fact]
    public void BothSignalsMustAgreeBeforeAnythingIsSkipped()
    {
        // Simple rubric, but the structure question says there is something to organise.
        var structureDisagrees = PromptClassifier.Interpret(Evaluation(
            needsStructure: 0.6, kind: "chore", kindP: 0.9, kindConfidence: 0.9,
            complexity: 0.1, complexityConfidence: 0.95));
        Assert.Equal(PromptPlanDecision.Refine, structureDisagrees.Decision);

        // Nothing to organise, but the rubric says it is substantial work.
        var complexityDisagrees = PromptClassifier.Interpret(Evaluation(
            needsStructure: 0.02, kind: "feature", kindP: 0.9, kindConfidence: 0.9,
            complexity: 1.8, complexityConfidence: 0.95));
        Assert.Equal(PromptPlanDecision.Refine, complexityDisagrees.Decision);
    }

    [Fact]
    public void ComplexityConfidenceIsRecordedButDoesNotGate()
    {
        // Measured on the development corpus, this confidence does not discriminate in this
        // workload: already-a-prompt cases spanned 0.35-0.86 and needs-organising cases 0.25-0.99,
        // fully overlapping. Gating on it cost five of eight correct skips and prevented none of
        // the dangerous errors, so the two independent signals carry the decision instead.
        var plan = PromptClassifier.Interpret(Evaluation(
            needsStructure: 0.02, kind: "chore", kindP: 0.9, kindConfidence: 0.9,
            complexity: 0.1, complexityConfidence: 0.35));

        Assert.Equal(PromptPlanDecision.SkipRefinement, plan.Decision);
        // Still reported, so the next tuning pass is not done blind like the first one was.
        Assert.Equal(0.35, plan.ComplexityConfidence!.Value, 6);
    }

    [Fact]
    public void AMissingComplexityAnswerNeverSkips()
    {
        // Without the second signal there is no agreement to rely on.
        var evaluation = new JevEvaluation("typesafe/jev-1.13-20260917", new Dictionary<string, JevAnswer>
        {
            [PromptClassifier.NeedsStructureId] =
                new(PromptClassifier.NeedsStructureId, null, null, null, 0.01),
        }, 400, 20, 0.00002, 400);

        Assert.Equal(PromptPlanDecision.Refine, PromptClassifier.Interpret(evaluation).Decision);
    }

    [Fact]
    public void APartialAnswerSetCannotSkip()
    {
        // The complexity question was dropped by per-answer validation; without it there is no
        // second signal, so skipping is off the table.
        var evaluation = new JevEvaluation("typesafe/jev-1.13-20260917", new Dictionary<string, JevAnswer>
        {
            [PromptClassifier.NeedsStructureId] =
                new(PromptClassifier.NeedsStructureId, null, null, null, 0.01),
        }, 400, 20, 0.00002, 400, new[] { "complexity:invalid_distribution" });

        var plan = PromptClassifier.Interpret(evaluation);

        Assert.Equal(PromptPlanDecision.Refine, plan.Decision);
        Assert.Null(plan.Complexity);
    }

    // ── The hint and the model choice ───────────────────────────────────

    [Fact]
    public void AnUncertainKindIsNotUsedAsAHint()
    {
        var plan = PromptClassifier.Interpret(Evaluation(
            needsStructure: 0.9, kind: "bug", kindP: Constants.PromptKindMinProbability - 0.05,
            kindConfidence: 0.9, complexity: 1.0, complexityConfidence: 0.8));

        Assert.Equal(RequestKind.Unknown, plan.Kind);
        Assert.Equal(string.Empty, PromptClassifier.HintFor(plan));
    }

    [Fact]
    public void AConfidentKindBecomesAOneLineHint()
    {
        var plan = PromptClassifier.Interpret(Evaluation(
            needsStructure: 0.9, kind: "refactor", kindP: 0.91, kindConfidence: 0.85,
            complexity: 1.2, complexityConfidence: 0.8));

        Assert.Equal(RequestKind.Refactor, plan.Kind);
        Assert.Contains("behaviour must stay identical", PromptClassifier.HintFor(plan));
    }

    [Fact]
    public void SubstantialWorkStartsOnTheQualityModel()
    {
        // Today every dictation starts on the fast model and only escalates after the completeness
        // guard trips, so the hardest requests are the ones that pay twice.
        var heavy = PromptClassifier.Interpret(Evaluation(
            needsStructure: 0.95, kind: "feature", kindP: 0.9, kindConfidence: 0.85,
            complexity: 1.9, complexityConfidence: 0.9));
        Assert.True(heavy.WantsQualityModel);

        var light = PromptClassifier.Interpret(Evaluation(
            needsStructure: 0.5, kind: "chore", kindP: 0.9, kindConfidence: 0.85,
            complexity: 0.8, complexityConfidence: 0.9));
        Assert.False(light.WantsQualityModel);
    }

    [Fact]
    public void TheDefaultPlanIsTodaysBehaviour()
    {
        var plan = PromptPlan.Default("whatever");
        Assert.Equal(PromptPlanDecision.Refine, plan.Decision);
        Assert.Equal(RequestKind.Unknown, plan.Kind);
        Assert.False(plan.WantsQualityModel);
        Assert.Equal(string.Empty, PromptClassifier.HintFor(plan));
    }
}
