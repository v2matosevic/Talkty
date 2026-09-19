using System.IO;
using System.Text.Json.Nodes;
using Talkty.App;
using Talkty.App.Models;
using Talkty.App.Services;
using Xunit;

namespace Talkty.Tests;

/// <summary>
/// The orchestrator: mode and key gating, size bounds, ledger interaction, status mapping, what
/// lands in the record, and which mode is allowed to interrupt the user. Every branch here decides
/// whether money is spent or whether the user is spoken to, so none of it should be reachable only
/// through a paid run.
/// </summary>
public class PromptFidelityServiceTests : IDisposable
{
    private readonly string _ledgerPath = Path.Combine(
        Path.GetTempPath(), $"talkty-fidelity-svc-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { if (File.Exists(_ledgerPath)) File.Delete(_ledgerPath); } catch { /* best effort */ }
    }

    private const string Transcript =
        "In src/lib/users.ts rename getUserById to loadUser. Do not deploy this to production.";
    private const string Rewrite =
        "**Task**\nIn src/lib/users.ts, rename getUserById to loadUser.";

    /// <summary>A transport that answers from a script, and counts how often it was asked.</summary>
    private sealed class StubClient : IJevDecisionClient
    {
        public int Calls { get; private set; }
        public JsonNode? LastState { get; private set; }
        public IReadOnlyList<JevQuestion>? LastQuestions { get; private set; }
        public string? LastKey { get; private set; }
        public Func<IReadOnlyList<JevQuestion>, JevResult> Answer { get; set; } =
            questions => JevResult.Fail(JevStatus.Unavailable, "timeout");

        public int? LastTimeoutMs { get; private set; }

        public Task<JevResult> EvaluateAsync(string apiKey, JsonNode state,
            IReadOnlyList<JevQuestion> questions, CancellationToken cancellationToken = default,
            int? timeoutMs = null)
        {
            LastTimeoutMs = timeoutMs;
            Calls++;
            LastKey = apiKey;
            LastState = state;
            LastQuestions = questions;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Answer(questions));
        }
    }

    private (PromptFidelityService service, StubClient client, JevFidelityLedger ledger) Build(
        PromptFidelityMode mode, string? key = "test-key")
    {
        var client = new StubClient();
        var ledger = new JevFidelityLedger(_ledgerPath);
        var service = new PromptFidelityService(client, ledger) { Mode = mode };
        service.SetApiKey(key);
        return (service, client, ledger);
    }

    /// <summary>Answers every clause question "omitted" with high certainty, and nothing added.</summary>
    private static JevResult ConfidentOmission(IReadOnlyList<JevQuestion> questions, double cost = 0.00005)
    {
        var answers = new Dictionary<string, JevAnswer>(StringComparer.Ordinal);
        foreach (var q in questions)
        {
            if (q.Id == PromptFidelityService.AddedQuestionId)
            {
                answers[q.Id] = new JevAnswer(q.Id, null, null, null, 0.01);
            }
            else if (q.Id == PromptFidelityService.AttentionQuestionId)
            {
                var p = q.Criteria.Keys.ToDictionary(k => k, _ => 0.0);
                p[PromptFidelityService.NoneOption] = 1.0;
                answers[q.Id] = new JevAnswer(q.Id, PromptFidelityService.NoneOption, 0.95, p, null);
            }
            else
            {
                var p = q.Criteria.Keys.ToDictionary(k => k, _ => 0.0);
                p["omitted"] = 0.97;
                p["preserved"] = 0.03;
                answers[q.Id] = new JevAnswer(q.Id, "omitted", 0.93, p, null);
            }
        }
        return new JevResult(JevStatus.Evaluated,
            new JevEvaluation("typesafe/jev-1.13-20260917", answers, 1200, 8, cost, 480), null);
    }

    // ── Gating: when nothing may be sent ─────────────────────────────────

    [Fact]
    public async Task OffModeSendsNothingAndRunsNoChecksAtAll()
    {
        var (service, client, ledger) = Build(PromptFidelityMode.Off);
        var outcome = await service.EvaluateAsync(Transcript, Rewrite);

        Assert.Equal(PromptFidelityStatus.Disabled, outcome.Status);
        Assert.Equal("mode_off", outcome.FailureCode);
        Assert.Equal(0, client.Calls);
        Assert.Empty(outcome.Concerns);
        // Off must not even reserve — an Off user's ledger stays empty.
        Assert.Equal(0, ledger.AllocatedTodayUsd);
        Assert.Empty(ledger.Records);
    }

    [Fact]
    public async Task WithoutAKeyTheExactLayerStillRunsAndNothingIsSpent()
    {
        var (service, client, ledger) = Build(PromptFidelityMode.RecordOnly, key: null);
        var outcome = await service.EvaluateAsync(Transcript, Rewrite);

        Assert.Equal(PromptFidelityStatus.CodeOnly, outcome.Status);
        Assert.Equal("no_api_key", outcome.FailureCode);
        Assert.Equal(0, client.Calls);
        Assert.Equal(0, ledger.AllocatedTodayUsd);
        // The dropped prohibition is still caught locally, for free.
        Assert.Contains(outcome.CodeConcerns, c => c.Kind == FidelityConcernKind.DroppedProhibition);
    }

    [Fact]
    public async Task OversizedInputSkipsTheModelLayerWithoutReserving()
    {
        var (service, client, ledger) = Build(PromptFidelityMode.RecordOnly);
        var huge = new string('x', Constants.JevMaxTranscriptChars + 1);

        var outcome = await service.EvaluateAsync(huge, Rewrite);

        Assert.Equal(PromptFidelityStatus.CodeOnly, outcome.Status);
        Assert.Equal("input_too_large", outcome.FailureCode);
        Assert.Equal(0, client.Calls);
        Assert.Equal(0, ledger.AllocatedTodayUsd);
    }

    [Fact]
    public async Task AnIdenticalPairIsNotPaidForTwice()
    {
        var (service, client, _) = Build(PromptFidelityMode.RecordOnly);
        client.Answer = q => ConfidentOmission(q);

        await service.EvaluateAsync(Transcript, Rewrite);
        var second = await service.EvaluateAsync(Transcript, Rewrite);

        Assert.Equal(1, client.Calls);
        Assert.Equal(PromptFidelityStatus.Skipped, second.Status);
        Assert.Equal("duplicate_suppressed", second.FailureCode);
    }

    // ── What is sent ─────────────────────────────────────────────────────

    [Fact]
    public async Task TheRequestCarriesTheDictationThePromptAndNothingElse()
    {
        var (service, client, _) = Build(PromptFidelityMode.RecordOnly);
        client.Answer = q => ConfidentOmission(q);

        await service.EvaluateAsync(Transcript, Rewrite);

        Assert.Equal("test-key", client.LastKey);
        var json = client.LastState!.ToJsonString();
        Assert.Contains("getUserById", json);
        Assert.Contains(Rewrite.Replace("\n", "\\n"), json);
        Assert.DoesNotContain("test-key", json);

        // One Choice per clause plus the added-requirement Noul and the attention Choice.
        var clauses = PromptFidelityAnalyzer.ExtractClauses(Transcript, Constants.JevMaxClauses);
        Assert.Equal(clauses.Count + 2, client.LastQuestions!.Count);
    }

    // ── Transport failures leave the flow alone ──────────────────────────

    [Theory]
    [InlineData(JevStatus.Unavailable, "timeout", PromptFidelityStatus.CodeOnly)]
    [InlineData(JevStatus.Unavailable, "http_401", PromptFidelityStatus.CodeOnly)]
    [InlineData(JevStatus.Invalid, "unknown_choice", PromptFidelityStatus.CodeOnly)]
    [InlineData(JevStatus.Cancelled, "cancelled", PromptFidelityStatus.Cancelled)]
    public async Task AFailedJudgmentKeepsTheCauseAndRaisesNoModelConcern(
        JevStatus jevStatus, string code, PromptFidelityStatus expected)
    {
        var (service, client, _) = Build(PromptFidelityMode.Review);
        client.Answer = _ => JevResult.Fail(jevStatus, code);
        var raised = 0;
        service.ConcernRaised += (_, _) => raised++;

        var outcome = await service.EvaluateAsync(Transcript, Rewrite);

        Assert.Equal(expected, outcome.Status);
        Assert.Equal(code, outcome.FailureCode);
        Assert.Empty(outcome.ModelConcerns);
        // The exact layer's finding still stands and is still shown — it never depended on the model.
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task AnUnknownChargeKeepsItsReservationRatherThanCountingAsFree()
    {
        var (service, client, ledger) = Build(PromptFidelityMode.RecordOnly);

        // A transport failure reports no cost at all, so the conservative reservation must stand.
        client.Answer = _ => JevResult.Fail(JevStatus.Unavailable, "timeout");
        await service.EvaluateAsync(Transcript, Rewrite);

        Assert.Equal(Constants.JevReservationMicroUsd / 1_000_000d, ledger.AllocatedTodayUsd, 9);
    }

    [Fact]
    public async Task AReportedChargeReplacesTheReservation()
    {
        var (service, client, ledger) = Build(PromptFidelityMode.RecordOnly);
        client.Answer = q => ConfidentOmission(q, cost: 0.00005);

        await service.EvaluateAsync(Transcript, Rewrite);

        Assert.Equal(0.00005, ledger.AllocatedTodayUsd, 9);
    }

    // ── Who is allowed to speak to the user ──────────────────────────────

    [Fact]
    public async Task RecordOnlyFindsConcernsButNeverRaisesOne()
    {
        var (service, client, ledger) = Build(PromptFidelityMode.RecordOnly);
        client.Answer = q => ConfidentOmission(q);
        var raised = 0;
        service.ConcernRaised += (_, _) => raised++;

        var outcome = await service.EvaluateAsync(Transcript, Rewrite);

        Assert.Equal(PromptFidelityStatus.Evaluated, outcome.Status);
        Assert.NotEmpty(outcome.Concerns);
        // This is the whole point of shadow mode: it learns, it does not interrupt.
        Assert.Equal(0, raised);
        Assert.Equal(0, ledger.Records[^1].Surfaced);
    }

    [Fact]
    public async Task ReviewRaisesTheConcernsItFound()
    {
        var (service, client, ledger) = Build(PromptFidelityMode.Review);
        client.Answer = q => ConfidentOmission(q);
        FidelityConcernEventArgs? seen = null;
        service.ConcernRaised += (_, e) => seen = e;

        var outcome = await service.EvaluateAsync(Transcript, Rewrite);

        Assert.NotNull(seen);
        Assert.Equal(outcome.Concerns.Count, seen!.Concerns.Count);
        Assert.Equal(outcome.Concerns.Count, ledger.Records[^1].Surfaced);
        Assert.True(outcome.Concerns.Count <= Constants.JevMaxSurfacedConcerns);
    }

    [Fact]
    public async Task AFaithfulRewriteRaisesNothingEvenInReview()
    {
        var (service, client, _) = Build(PromptFidelityMode.Review);
        client.Answer = questions =>
        {
            var answers = new Dictionary<string, JevAnswer>(StringComparer.Ordinal);
            foreach (var q in questions)
            {
                if (q.Id == PromptFidelityService.AddedQuestionId)
                    answers[q.Id] = new JevAnswer(q.Id, null, null, null, 0.02);
                else
                {
                    var p = q.Criteria.Keys.ToDictionary(k => k, _ => 0.0);
                    var winner = q.Id == PromptFidelityService.AttentionQuestionId
                        ? PromptFidelityService.NoneOption : "preserved";
                    p[winner] = 0.98;
                    p[q.Criteria.Keys.First(k => k != winner)] = 0.02;
                    answers[q.Id] = new JevAnswer(q.Id, winner, 0.96, p, null);
                }
            }
            return new JevResult(JevStatus.Evaluated,
                new JevEvaluation("typesafe/jev-1.13-20260917", answers, 900, 6, 0.00004, 460), null);
        };
        var raised = 0;
        service.ConcernRaised += (_, _) => raised++;

        // A rewrite with no exact losses either.
        var outcome = await service.EvaluateAsync(
            "Add a loading spinner to the export button.",
            "Add a loading spinner to the export button while the download runs.");

        Assert.Equal(PromptFidelityStatus.Evaluated, outcome.Status);
        Assert.Empty(outcome.Concerns);
        Assert.Equal(0, raised);
    }

    // ── The record ───────────────────────────────────────────────────────

    [Fact]
    public async Task TheRecordCarriesTheComparisonAndNoUserText()
    {
        var (service, client, ledger) = Build(PromptFidelityMode.RecordOnly);
        client.Answer = q => ConfidentOmission(q);

        await service.EvaluateAsync(Transcript, Rewrite);

        var record = Assert.Single(ledger.Records);
        Assert.Equal("RecordOnly", record.Mode);
        Assert.Equal("Evaluated", record.JevStatus);
        Assert.Equal("typesafe/jev-1.13-20260917", record.Model);
        Assert.Equal(1200, record.InputTokens);
        Assert.Equal(0.00005, record.CostUsd!.Value, 9);
        Assert.Equal(PromptFidelityService.Hash(Transcript), record.TranscriptHash);
        Assert.Equal(Transcript.Length, record.TranscriptLength);
        // The baseline verdict is recorded on every evaluation, so the comparison stays honest.
        Assert.Equal(PromptRefinementService.IsSuspectedSummary(Transcript, Rewrite), record.BaselineGuardTripped);
        Assert.Contains(record.Concerns, c => c.StartsWith("DroppedProhibition"));

        var onDisk = File.ReadAllText(_ledgerPath);
        Assert.DoesNotContain("getUserById", onDisk);
        Assert.DoesNotContain("Do not deploy", onDisk);
    }

    [Fact]
    public async Task EveryEvaluationIsRecordedIncludingTheOnesThatNeverLeftTheMachine()
    {
        var (service, client, ledger) = Build(PromptFidelityMode.RecordOnly, key: null);
        await service.EvaluateAsync(Transcript, Rewrite);

        var record = Assert.Single(ledger.Records);
        Assert.Equal("CodeOnly", record.JevStatus);
        Assert.Equal("no_api_key", record.FailureCode);
        Assert.Null(record.CostUsd);
        Assert.Equal(0, client.Calls);
    }

    // ── Cancellation ─────────────────────────────────────────────────────

    [Fact]
    public async Task CancelPendingStopsAnInFlightEvaluation()
    {
        var client = new StubClient();
        var ledger = new JevFidelityLedger(_ledgerPath);
        var service = new PromptFidelityService(client, ledger) { Mode = PromptFidelityMode.Review };
        service.SetApiKey("test-key");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Answer = _ =>
        {
            entered.SetResult();
            // The real client observes the token; the stub throws the same way the transport would.
            throw new OperationCanceledException();
        };

        var evaluation = service.EvaluateAsync(Transcript, Rewrite);
        await entered.Task;
        service.CancelPending();
        var outcome = await evaluation;

        // A cancelled check must not become a concern, and must not be swallowed as a success.
        Assert.NotEqual(PromptFidelityStatus.Evaluated, outcome.Status);
        Assert.Empty(outcome.ModelConcerns);
    }

    [Fact]
    public async Task EmptyInputIsNeverSent()
    {
        var (service, client, _) = Build(PromptFidelityMode.Review);

        Assert.Equal("empty_input", (await service.EvaluateAsync("", Rewrite)).FailureCode);
        Assert.Equal("empty_input", (await service.EvaluateAsync(Transcript, "   ")).FailureCode);
        Assert.Equal(0, client.Calls);
    }
}
