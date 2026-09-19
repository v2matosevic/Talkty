using System.Text.Json;
using System.Text.Json.Nodes;
using Talkty.App;
using Talkty.App.Services;
using Xunit;

namespace Talkty.Tests;

/// <summary>
/// The contract changes that came out of reading the model properly and out of peer field data:
/// structured JSON criteria, the Score primitive, a rounding-sized distribution window, and
/// per-question rather than per-response answer rejection.
/// </summary>
public class JevDecisionContractTests
{
    private static JsonNode State() => new JsonObject { ["dictation"] = "rename it" };

    private static JevQuestion StructuredChoice(string id = "kind") => new(
        id, JevQuestionKind.Choice,
        new JsonObject { ["question"] = "Which kind?", ["focus"] = "primary request only" },
        new Dictionary<string, JsonNode?>
        {
            ["bug"] = new JsonObject { ["what"] = "behaves wrongly", ["not_for"] = "does not exist yet" },
            ["chore"] = new JsonObject { ["what"] = "one mechanical edit" }
        });

    private static JevQuestion ScoreQuestion(string id = "complexity") => new(
        id,
        new JsonObject { ["question"] = "How much work?" },
        new JsonNode[]
        {
            new JsonObject { ["summary"] = "small" },
            new JsonObject { ["summary"] = "medium" },
            new JsonObject { ["summary"] = "large" }
        });

    // ── Structured guidance reaches the wire ────────────────────────────

    [Fact]
    public void StructuredCriteriaAndInstructionsSerializeAsObjects()
    {
        var (json, failure) = JevDecisionClient.BuildRequest(State(), new[] { StructuredChoice() });
        Assert.Null(failure);

        using var doc = JsonDocument.Parse(json!);
        var q = doc.RootElement.GetProperty("questions").GetProperty("kind");

        // Not a stringified blob: real JSON the model can read as structure.
        Assert.Equal(JsonValueKind.Object, q.GetProperty("instructions").ValueKind);
        Assert.Equal("primary request only", q.GetProperty("instructions").GetProperty("focus").GetString());
        Assert.Equal("does not exist yet",
            q.GetProperty("criteria").GetProperty("bug").GetProperty("not_for").GetString());
    }

    [Fact]
    public void PlainStringQuestionsStillSerializeAsStrings()
    {
        // The convenience constructor keeps every existing caller working unchanged.
        var plain = new JevQuestion("c1", JevQuestionKind.Choice, "Evaluate c1",
            new Dictionary<string, string> { ["preserved"] = "kept", ["omitted"] = "gone" });

        var (json, failure) = JevDecisionClient.BuildRequest(State(), new[] { plain });
        Assert.Null(failure);

        using var doc = JsonDocument.Parse(json!);
        var q = doc.RootElement.GetProperty("questions").GetProperty("c1");
        Assert.Equal(JsonValueKind.String, q.GetProperty("instructions").ValueKind);
        Assert.Equal("kept", q.GetProperty("criteria").GetProperty("preserved").GetString());
    }

    [Fact]
    public void ScoreSerializesOrderedLevelsAsAnArray()
    {
        var (json, failure) = JevDecisionClient.BuildRequest(State(), new[] { ScoreQuestion() });
        Assert.Null(failure);

        using var doc = JsonDocument.Parse(json!);
        var q = doc.RootElement.GetProperty("questions").GetProperty("complexity");
        Assert.Equal("score", q.GetProperty("type").GetString());
        // Order is the meaning of a rubric, so it must be an array and not an object.
        Assert.Equal(JsonValueKind.Array, q.GetProperty("criteria").ValueKind);
        Assert.Equal(3, q.GetProperty("criteria").GetArrayLength());
    }

    [Fact]
    public void AScaleNeedsAtLeastTwoRungs()
    {
        var oneRung = new JevQuestion("x", new JsonObject { ["question"] = "?" },
            new JsonNode[] { new JsonObject { ["summary"] = "only" } });
        Assert.Equal("invalid_questions", JevDecisionClient.BuildRequest(State(), new[] { oneRung }).failure);
    }

    // ── Score answers ───────────────────────────────────────────────────

    [Fact]
    public void AScoreBetweenLevelsIsAValidAnswer()
    {
        // 0.91 on a three-rung rubric is a real answer, not a rounding error to snap away.
        var body =
            """
            {"model":"typesafe/jev-1.13-20260917",
             "answers":{"complexity":{"type":"score","score":0.91,"confidence":0.8,
                                      "probabilities":{"0":0.11,"1":0.87,"2":0.02}}},
             "usage":{"input_tokens":683,"output_tokens":71,"cost":0.0000287}}
            """;
        var (evaluation, failure) = JevDecisionClient.Parse(body, new[] { ScoreQuestion() }, 0);

        Assert.Null(failure);
        var answer = evaluation!.Answers["complexity"];
        Assert.Equal(0.91, answer.Score!.Value, 6);
        Assert.Equal(0.8, answer.Confidence!.Value, 6);
        Assert.Equal(0.87, answer.Probabilities!["1"], 6);
        Assert.Null(answer.Choice);
    }

    [Fact]
    public void AScoreOutsideTheRubricIsRejected()
    {
        var body =
            """
            {"model":"typesafe/jev-1.13","answers":{"complexity":{"type":"score","score":4.0,"confidence":0.8,
             "probabilities":{"0":0.1,"1":0.8,"2":0.1}}},"usage":{"input_tokens":1,"output_tokens":1}}
            """;
        Assert.Equal("invalid_score", JevDecisionClient.Parse(body, new[] { ScoreQuestion() }, 0).failure);
    }

    // ── Distribution tolerance ──────────────────────────────────────────

    [Theory]
    // Two-decimal rounding over several options routinely misses 1.0. A peer measured 2.4% of 292
    // live calls rejected by the old 0.005 window, skewed toward Croatian.
    [InlineData(0.62, 0.36, true)]   // sums to 0.98
    [InlineData(0.63, 0.39, true)]   // sums to 1.02
    [InlineData(0.50, 0.20, false)]  // sums to 0.70 — not rounding, genuinely not a distribution
    public void RoundingDriftIsToleratedButRealNonsenseIsNot(double first, double second, bool accepted)
    {
        var question = new JevQuestion("c1", JevQuestionKind.Choice, "Evaluate c1",
            new Dictionary<string, string> { ["preserved"] = "kept", ["omitted"] = "gone" });

        var body =
            """
            {"model":"typesafe/jev-1.13","answers":{"c1":{"type":"choice","choice":"preserved","confidence":0.9,
             "probabilities":{"preserved":P1,"omitted":P2}}},
             "usage":{"input_tokens":1,"output_tokens":1}}
            """
            .Replace("P1", first.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("P2", second.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var (evaluation, failure) = JevDecisionClient.Parse(body, new[] { question }, 0);

        if (accepted)
        {
            Assert.Null(failure);
            // Deliberately NOT renormalized: a short sum understates the winner, so a minimum
            // threshold gets harder to pass, never easier.
            Assert.Equal(first, evaluation!.Answers["c1"].SelectedProbability, 6);
        }
        else
        {
            Assert.Equal("invalid_distribution", failure);
        }
    }

    [Fact]
    public void ToleranceStaysWellInsideWhatWouldChangeAThresholdDecision()
    {
        // The window is a rounding allowance. If it ever approached the fidelity gate's own margin
        // it would start deciding outcomes by itself.
        Assert.True(Constants.JevDistributionTolerance < Constants.JevFidelityMinProbability / 4);
    }

    // ── Partial results ─────────────────────────────────────────────────

    [Fact]
    public void OneBadAnswerDoesNotDiscardTheWholeBatch()
    {
        // The case a peer raised: a fan-out request rides many independent questions, and losing
        // eleven good verdicts because a twelfth drifted is the worse trade.
        var questions = new[]
        {
            new JevQuestion("c1", JevQuestionKind.Choice, "Evaluate c1",
                new Dictionary<string, string> { ["preserved"] = "kept", ["omitted"] = "gone" }),
            new JevQuestion("c2", JevQuestionKind.Choice, "Evaluate c2",
                new Dictionary<string, string> { ["preserved"] = "kept", ["omitted"] = "gone" }),
        };

        var body =
            """
            {"model":"typesafe/jev-1.13",
             "answers":{"c1":{"type":"choice","choice":"omitted","confidence":0.9,
                              "probabilities":{"omitted":0.95,"preserved":0.05}},
                        "c2":{"type":"choice","choice":"invented","confidence":0.9,
                              "probabilities":{"omitted":0.5,"preserved":0.5}}},
             "usage":{"input_tokens":100,"output_tokens":4}}
            """;
        var (evaluation, failure) = JevDecisionClient.Parse(body, questions, 0);

        Assert.Null(failure);
        Assert.Equal("omitted", evaluation!.Answers["c1"].Choice);
        Assert.False(evaluation.Answers.ContainsKey("c2"));
        Assert.True(evaluation.IsPartial);
        Assert.Contains("c2:unknown_choice", evaluation.RejectedAnswers!);
    }

    [Fact]
    public void ACleanResponseIsNotMarkedPartial()
    {
        var question = new JevQuestion("c1", JevQuestionKind.Choice, "Evaluate c1",
            new Dictionary<string, string> { ["preserved"] = "kept", ["omitted"] = "gone" });
        var body =
            """
            {"model":"typesafe/jev-1.13","answers":{"c1":{"type":"choice","choice":"omitted","confidence":0.9,
             "probabilities":{"omitted":0.95,"preserved":0.05}}},"usage":{"input_tokens":1,"output_tokens":1}}
            """;
        var (evaluation, _) = JevDecisionClient.Parse(body, new[] { question }, 0);
        Assert.False(evaluation!.IsPartial);
    }

    [Fact]
    public void WhenEveryAnswerIsUnusableTheResponseStillFails()
    {
        // Partial tolerance must not become "accept anything". Nothing usable is a real failure,
        // and the caller still gets the cause rather than an empty success.
        var question = new JevQuestion("c1", JevQuestionKind.Choice, "Evaluate c1",
            new Dictionary<string, string> { ["preserved"] = "kept", ["omitted"] = "gone" });
        var body =
            """
            {"model":"typesafe/jev-1.13","answers":{"c1":{"type":"choice","choice":"invented","confidence":0.9,
             "probabilities":{"omitted":0.5,"preserved":0.5}}},"usage":{"input_tokens":1,"output_tokens":1}}
            """;
        var (evaluation, failure) = JevDecisionClient.Parse(body, new[] { question }, 0);

        Assert.Null(evaluation);
        Assert.Equal("unknown_choice", failure);
    }

    [Fact]
    public void ResponseLevelProblemsStayResponseLevel()
    {
        var question = new JevQuestion("c1", JevQuestionKind.Choice, "Evaluate c1",
            new Dictionary<string, string> { ["preserved"] = "kept", ["omitted"] = "gone" });

        // An unexpected model build invalidates every threshold we qualified, so nothing survives.
        var wrongModel =
            """
            {"model":"typesafe/jev-9.9","answers":{"c1":{"type":"choice","choice":"omitted","confidence":0.9,
             "probabilities":{"omitted":0.95,"preserved":0.05}}},"usage":{"input_tokens":1,"output_tokens":1}}
            """;
        Assert.Equal("unexpected_model", JevDecisionClient.Parse(wrongModel, new[] { question }, 0).failure);

        // Usage is how spending is accounted for; without it the call is unbilled as far as we know.
        var noUsage =
            """
            {"model":"typesafe/jev-1.13","answers":{"c1":{"type":"choice","choice":"omitted","confidence":0.9,
             "probabilities":{"omitted":0.95,"preserved":0.05}}}}
            """;
        Assert.Equal("missing_usage", JevDecisionClient.Parse(noUsage, new[] { question }, 0).failure);
    }
}
