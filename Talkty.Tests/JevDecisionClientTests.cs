using System.Text.Json;
using System.Text.Json.Nodes;
using Talkty.App;
using Talkty.App.Services;
using Xunit;

namespace Talkty.Tests;

/// <summary>
/// Locks the Jev decision contract: what we send, and what we refuse to believe.
///
/// Every rejection here is deliberate. A decision model whose answer we half-trust is worse than
/// no decision model — an unknown option, a distribution that does not sum to one, a winning option
/// that is not the maximum, or an unexpected model build all invalidate the thresholds the policy
/// layer relies on.
/// </summary>
public class JevDecisionClientTests
{
    private static IReadOnlyList<JevQuestion> Questions() => new[]
    {
        new JevQuestion("c1", JevQuestionKind.Choice, "Evaluate clause c1", new Dictionary<string, string>
        {
            ["preserved"] = "kept",
            ["omitted"] = "gone"
        })
    };

    private static string GoodResponse() =>
        """
        {"model":"typesafe/jev-1.13-20260917",
         "answers":{"c1":{"type":"choice","choice":"omitted","confidence":0.9,
                          "probabilities":{"omitted":0.95,"preserved":0.05}}},
         "usage":{"input_tokens":1200,"output_tokens":8,"cost":0.0000504}}
        """;

    private static JsonNode State() => new JsonObject { ["prompt"] = "x" };

    // ── Request shape ───────────────────────────────────────────────────

    [Fact]
    public void Request_PinsRouteModelAndPrivacyPosture()
    {
        var (json, failure) = JevDecisionClient.BuildRequest(State(), Questions());
        Assert.Null(failure);
        Assert.NotNull(json);

        using var doc = JsonDocument.Parse(json!);
        Assert.Equal("typesafe/jev-1.13", doc.RootElement.GetProperty("model").GetString());

        var provider = doc.RootElement.GetProperty("provider");
        Assert.Equal("deny", provider.GetProperty("data_collection").GetString());
        Assert.True(provider.GetProperty("zdr").GetBoolean());
        Assert.False(provider.GetProperty("allow_fallbacks").GetBoolean());

        // The live-qualified route. The generated OpenAPI pair composes /api/v1/api/alpha/decisions,
        // which 404s — this assertion is what stops someone "correcting" it back.
        Assert.Equal("https://openrouter.ai/api/alpha/decisions", JevDecisionClient.Endpoint);
    }

    [Fact]
    public void Request_RejectsOversizedState()
    {
        var huge = new JsonObject { ["prompt"] = new string('x', Constants.JevMaxRequestBytes) };
        var (json, failure) = JevDecisionClient.BuildRequest(huge, Questions());
        Assert.Null(json);
        Assert.Equal("input_too_large", failure);
    }

    [Fact]
    public void Request_RejectsMalformedQuestions()
    {
        // No questions at all.
        Assert.Equal("invalid_questions", JevDecisionClient.BuildRequest(State(), Array.Empty<JevQuestion>()).failure);

        // A Noul must be exactly true/false — anything else is not a single proposition.
        var badNoul = new[]
        {
            new JevQuestion("added", JevQuestionKind.Noul, "Added?", new Dictionary<string, string>
            {
                ["yes"] = "a", ["no"] = "b"
            })
        };
        Assert.Equal("invalid_questions", JevDecisionClient.BuildRequest(State(), badNoul).failure);

        // A Choice needs at least two options, or there is nothing to choose.
        var oneOption = new[]
        {
            new JevQuestion("c1", JevQuestionKind.Choice, "Pick", new Dictionary<string, string> { ["only"] = "x" })
        };
        Assert.Equal("invalid_questions", JevDecisionClient.BuildRequest(State(), oneOption).failure);

        // Duplicate IDs would make the answer set ambiguous.
        var duplicate = Questions().Concat(Questions()).ToList();
        Assert.Equal("invalid_questions", JevDecisionClient.BuildRequest(State(), duplicate).failure);
    }

    [Fact]
    public void Request_SerializesNoulAndChoiceTypes()
    {
        var questions = new[]
        {
            Questions()[0],
            new JevQuestion("added", JevQuestionKind.Noul, "Added?", new Dictionary<string, string>
            {
                ["true"] = "yes", ["false"] = "no"
            })
        };
        var (json, _) = JevDecisionClient.BuildRequest(State(), questions);
        using var doc = JsonDocument.Parse(json!);
        var q = doc.RootElement.GetProperty("questions");
        Assert.Equal("choice", q.GetProperty("c1").GetProperty("type").GetString());
        Assert.Equal("noul", q.GetProperty("added").GetProperty("type").GetString());
    }

    // ── Response validation ─────────────────────────────────────────────

    [Fact]
    public void Parse_AcceptsValidatedChoice()
    {
        var (evaluation, failure) = JevDecisionClient.Parse(GoodResponse(), Questions(), 512);
        Assert.Null(failure);
        Assert.NotNull(evaluation);
        Assert.Equal("typesafe/jev-1.13-20260917", evaluation!.Model);
        Assert.Equal("omitted", evaluation.Answers["c1"].Choice);
        Assert.Equal(0.95, evaluation.Answers["c1"].SelectedProbability, 6);
        Assert.Equal(1200, evaluation.InputTokens);
        Assert.Equal(0.0000504, evaluation.CostUsd!.Value, 12);
        Assert.Equal(512, evaluation.ElapsedMs);
    }

    [Theory]
    // An option we never offered: the model cannot invent an answer space.
    [InlineData("""{"model":"typesafe/jev-1.13","answers":{"c1":{"type":"choice","choice":"invented","confidence":0.9,"probabilities":{"omitted":0.95,"preserved":0.05}}},"usage":{"input_tokens":1,"output_tokens":1}}""", "unknown_choice")]
    // The winning option is not the maximum of the distribution — choice and probabilities disagree.
    [InlineData("""{"model":"typesafe/jev-1.13","answers":{"c1":{"type":"choice","choice":"preserved","confidence":0.9,"probabilities":{"omitted":0.95,"preserved":0.05}}},"usage":{"input_tokens":1,"output_tokens":1}}""", "choice_not_maximum")]
    // The distribution does not sum to one.
    [InlineData("""{"model":"typesafe/jev-1.13","answers":{"c1":{"type":"choice","choice":"omitted","confidence":0.9,"probabilities":{"omitted":0.5,"preserved":0.1}}},"usage":{"input_tokens":1,"output_tokens":1}}""", "invalid_distribution")]
    // Missing confidence: nothing to threshold on.
    [InlineData("""{"model":"typesafe/jev-1.13","answers":{"c1":{"type":"choice","choice":"omitted","probabilities":{"omitted":0.95,"preserved":0.05}}},"usage":{"input_tokens":1,"output_tokens":1}}""", "invalid_probability")]
    // A future model build we have never qualified.
    [InlineData("""{"model":"typesafe/jev-2.0","answers":{"c1":{"type":"choice","choice":"omitted","confidence":0.9,"probabilities":{"omitted":0.95,"preserved":0.05}}},"usage":{"input_tokens":1,"output_tokens":1}}""", "unexpected_model")]
    // Answer count does not match the questions asked.
    [InlineData("""{"model":"typesafe/jev-1.13","answers":{},"usage":{"input_tokens":1,"output_tokens":1}}""", "mismatched_answers")]
    // A noul answer to a choice question.
    [InlineData("""{"model":"typesafe/jev-1.13","answers":{"c1":{"type":"noul","noul":0.9}},"usage":{"input_tokens":1,"output_tokens":1}}""", "mismatched_type")]
    // Usage is how we account for spend; without it the request is unbilled as far as we can tell.
    [InlineData("""{"model":"typesafe/jev-1.13","answers":{"c1":{"type":"choice","choice":"omitted","confidence":0.9,"probabilities":{"omitted":0.95,"preserved":0.05}}}}""", "missing_usage")]
    [InlineData("not json at all", "invalid_json")]
    public void Parse_RejectsInvalidResponses(string body, string expectedFailure)
    {
        var (evaluation, failure) = JevDecisionClient.Parse(body, Questions(), 0);
        Assert.Null(evaluation);
        Assert.Equal(expectedFailure, failure);
    }

    [Fact]
    public void Parse_AcceptsBothTokenFieldSpellings()
    {
        var camel =
            """
            {"model":"typesafe/jev-1.13",
             "answers":{"c1":{"type":"choice","choice":"omitted","confidence":0.9,
                              "probabilities":{"omitted":0.95,"preserved":0.05}}},
             "usage":{"inputTokens":700,"outputTokens":4}}
            """;
        var (evaluation, failure) = JevDecisionClient.Parse(camel, Questions(), 0);
        Assert.Null(failure);
        Assert.Equal(700, evaluation!.InputTokens);
        Assert.Equal(4, evaluation.OutputTokens);
    }

    [Fact]
    public void Parse_RejectsContradictoryTokenFields()
    {
        var conflicting =
            """
            {"model":"typesafe/jev-1.13",
             "answers":{"c1":{"type":"choice","choice":"omitted","confidence":0.9,
                              "probabilities":{"omitted":0.95,"preserved":0.05}}},
             "usage":{"input_tokens":700,"inputTokens":701,"output_tokens":4}}
            """;
        var (evaluation, failure) = JevDecisionClient.Parse(conflicting, Questions(), 0);
        Assert.Null(evaluation);
        Assert.Equal("conflicting_usage", failure);
    }

    [Fact]
    public void Parse_KeepsMissingCostUnknownRatherThanZero()
    {
        var noCost =
            """
            {"model":"typesafe/jev-1.13",
             "answers":{"c1":{"type":"choice","choice":"omitted","confidence":0.9,
                              "probabilities":{"omitted":0.95,"preserved":0.05}}},
             "usage":{"input_tokens":700,"output_tokens":4}}
            """;
        var (evaluation, failure) = JevDecisionClient.Parse(noCost, Questions(), 0);
        Assert.Null(failure);
        Assert.Null(evaluation!.CostUsd);
    }

    [Fact]
    public void Parse_ValidatesNoulRangeAndReportsNoConfidence()
    {
        var questions = new[]
        {
            new JevQuestion("added", JevQuestionKind.Noul, "Added?", new Dictionary<string, string>
            {
                ["true"] = "yes", ["false"] = "no"
            })
        };

        var ok = """{"model":"typesafe/jev-1.13","answers":{"added":{"type":"noul","noul":0.82}},"usage":{"input_tokens":5,"output_tokens":1}}""";
        var (evaluation, failure) = JevDecisionClient.Parse(ok, questions, 0);
        Assert.Null(failure);
        Assert.Equal(0.82, evaluation!.Answers["added"].Noul!.Value, 6);
        // Noul reports a yes probability only — inventing a confidence would invent a statistic.
        Assert.Null(evaluation.Answers["added"].Confidence);

        var outOfRange = """{"model":"typesafe/jev-1.13","answers":{"added":{"type":"noul","noul":1.1}},"usage":{"input_tokens":5,"output_tokens":1}}""";
        Assert.Equal("invalid_probability", JevDecisionClient.Parse(outOfRange, questions, 0).failure);
    }

    [Fact]
    public async Task Evaluate_WithoutKeyIsDisabledAndSendsNothing()
    {
        var result = await new JevDecisionClient().EvaluateAsync("", State(), Questions());
        Assert.Equal(JevStatus.Disabled, result.Status);
        Assert.Equal("no_api_key", result.FailureCode);
    }
}
