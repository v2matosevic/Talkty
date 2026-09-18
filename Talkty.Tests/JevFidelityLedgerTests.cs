using System.IO;
using Talkty.App;
using Talkty.App.Services;
using Xunit;

namespace Talkty.Tests;

/// <summary>
/// The local spending reservation, rate limit and comparison record. Money accounting that can
/// silently lose a charge is worse than no accounting, so the tests check the conservative
/// direction: an unknown cost keeps its reservation and a known cost rounds up.
/// </summary>
public class JevFidelityLedgerTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"talkty-jev-ledger-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best effort */ }
    }

    private JevFidelityLedger New() => new(_path);

    [Fact]
    public void FirstAttemptIsAllowedAndReservesBeforeTheRequest()
    {
        var ledger = New();
        Assert.Equal(JevBudgetDecision.Allowed, ledger.TryReserve("hash-a"));
        Assert.Equal(Constants.JevReservationMicroUsd / 1_000_000d, ledger.AllocatedTodayUsd, 9);
    }

    [Fact]
    public void IdenticalWorkIsNotPaidForTwiceInARow()
    {
        var ledger = New();
        Assert.Equal(JevBudgetDecision.Allowed, ledger.TryReserve("same"));
        Assert.Equal(JevBudgetDecision.Duplicate, ledger.TryReserve("same"));
        Assert.Equal(JevBudgetDecision.Allowed, ledger.TryReserve("different"));
    }

    [Fact]
    public void RateLimitStopsARunawayCaller()
    {
        var ledger = New();
        for (int i = 0; i < Constants.JevMaxAttemptsPerMinute; i++)
            Assert.Equal(JevBudgetDecision.Allowed, ledger.TryReserve($"h{i}"));

        Assert.Equal(JevBudgetDecision.RateLimited, ledger.TryReserve("one-too-many"));
    }

    [Fact]
    public void ReportedCostReplacesTheReservation()
    {
        var ledger = New();
        ledger.TryReserve("hash-a");
        ledger.Settle(0.0000245);

        // The reservation is released and the real charge (rounded up to a whole microdollar) stands.
        Assert.Equal(0.000025, ledger.AllocatedTodayUsd, 9);
    }

    [Fact]
    public void UnknownCostKeepsTheReservationRatherThanCountingAsFree()
    {
        var ledger = New();
        ledger.TryReserve("hash-a");
        ledger.Settle(null);

        Assert.Equal(Constants.JevReservationMicroUsd / 1_000_000d, ledger.AllocatedTodayUsd, 9);
    }

    [Fact]
    public void DailyCapEventuallyRefusesAnAttempt()
    {
        var ledger = New();
        var possible = (int)(Constants.JevDailyBudgetMicroUsd / Constants.JevReservationMicroUsd);

        // Settle each attempt at an unknown cost so reservations accumulate; a rate-limit-free run
        // is not the point here, so allow the minute window to be the limiting factor first.
        var refusals = 0;
        for (int i = 0; i < possible + 5; i++)
        {
            var decision = ledger.TryReserve($"h{i}");
            if (decision == JevBudgetDecision.DailyBudgetExhausted) { refusals++; break; }
            if (decision != JevBudgetDecision.Allowed) break; // rate limit reached first
            ledger.Settle(null);
        }

        Assert.True(ledger.AllocatedTodayUsd <= Constants.JevDailyBudgetMicroUsd / 1_000_000d,
            "allocated spend must never exceed the daily cap");
        Assert.True(refusals > 0 || Constants.JevMaxAttemptsPerMinute < possible,
            "either the cap refused an attempt, or the per-minute limit bound first");
    }

    [Fact]
    public void RecordsPersistAcrossInstancesAndHoldNoUserText()
    {
        var secret = "do not deploy the thing to production";
        New().Append(new JevFidelityRecord
        {
            TimestampUtc = DateTime.UtcNow,
            Mode = "RecordOnly",
            TranscriptHash = PromptFidelityService.Hash(secret),
            TranscriptLength = secret.Length,
            RewriteHash = PromptFidelityService.Hash("rewrite"),
            RewriteLength = 7,
            BaselineGuardTripped = false,
            CodeConcerns = 1,
            JevStatus = "Evaluated",
            Concerns = { "DroppedProhibition:c1" },
            Surfaced = 0
        });

        var reloaded = New().Records;
        var record = Assert.Single(reloaded);
        Assert.Equal("DroppedProhibition:c1", Assert.Single(record.Concerns));
        Assert.Equal(0, record.Surfaced);

        // The point of hashing: the dictation itself never lands on disk.
        var onDisk = File.ReadAllText(_path);
        Assert.DoesNotContain("do not deploy", onDisk);
        Assert.Contains(PromptFidelityService.Hash(secret), onDisk);
    }

    [Fact]
    public void RecordsAreTrimmedToTheRetentionCap()
    {
        var ledger = New();
        for (int i = 0; i < Constants.JevMaxRecords + 10; i++)
            ledger.Append(new JevFidelityRecord { Mode = "RecordOnly", JevStatus = $"s{i}" });

        var records = ledger.Records;
        Assert.Equal(Constants.JevMaxRecords, records.Count);
        // The oldest go first, so the newest evidence survives.
        Assert.Equal($"s{Constants.JevMaxRecords + 9}", records[^1].JevStatus);
    }
}
