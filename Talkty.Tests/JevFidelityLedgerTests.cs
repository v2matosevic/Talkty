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

    /// <summary>
    /// Seeds the ledger file directly. The cap is unreachable through TryReserve alone — the
    /// per-minute limit (20) bites long before 125 reservations — so the earlier version of this
    /// test could never fail. Seeding state is the only way to exercise the branch for real.
    /// </summary>
    private void Seed(string day, long reserved, long spent,
        string[]? hashes = null, long[]? hashTimes = null)
    {
        var state = new
        {
            Day = day,
            ReservedMicroUsd = reserved,
            SpentMicroUsd = spent,
            Attempts = Array.Empty<long>(),
            RecentHashes = hashes ?? Array.Empty<string>(),
            RecentHashTimes = hashTimes ?? Array.Empty<long>(),
            Records = Array.Empty<object>()
        };
        File.WriteAllText(_path, System.Text.Json.JsonSerializer.Serialize(state));
    }

    private static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    [Fact]
    public void TheDailyCapRefusesTheAttemptThatWouldBreachIt()
    {
        // One reservation short of the cap: the next attempt must be refused outright.
        Seed(Today, reserved: 0, spent: Constants.JevDailyBudgetMicroUsd - Constants.JevReservationMicroUsd + 1);

        Assert.Equal(JevBudgetDecision.DailyBudgetExhausted, New().TryReserve("over-the-line"));
    }

    [Fact]
    public void TheLastAffordableAttemptIsStillAllowed()
    {
        // Exactly enough room for one more reservation — the boundary must not be off by one.
        Seed(Today, reserved: 0, spent: Constants.JevDailyBudgetMicroUsd - Constants.JevReservationMicroUsd);

        var ledger = New();
        Assert.Equal(JevBudgetDecision.Allowed, ledger.TryReserve("the-last-one"));
        Assert.Equal(Constants.JevDailyBudgetMicroUsd / 1_000_000d, ledger.AllocatedTodayUsd, 9);
        // And nothing beyond it.
        Assert.Equal(JevBudgetDecision.DailyBudgetExhausted, ledger.TryReserve("one-too-many"));
    }

    [Fact]
    public void OutstandingReservationsCountTowardTheCapAsWellAsSettledSpend()
    {
        // A crash that left reservations standing must not hand the next day's budget back early.
        Seed(Today, reserved: Constants.JevDailyBudgetMicroUsd, spent: 0);

        Assert.Equal(JevBudgetDecision.DailyBudgetExhausted, New().TryReserve("after-a-crash"));
    }

    [Fact]
    public void ANewDayResetsTheAllocation()
    {
        Seed(DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd"),
            reserved: Constants.JevDailyBudgetMicroUsd, spent: Constants.JevDailyBudgetMicroUsd);

        var ledger = New();
        Assert.Equal(JevBudgetDecision.Allowed, ledger.TryReserve("fresh-day"));
        Assert.Equal(Constants.JevReservationMicroUsd / 1_000_000d, ledger.AllocatedTodayUsd, 9);
    }

    [Fact]
    public void DuplicateSuppressionExpires()
    {
        var stale = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Constants.JevDuplicateSuppressionSeconds - 5;
        Seed(Today, 0, 0, hashes: new[] { "same" }, hashTimes: new[] { stale });

        // The same work is fair to re-evaluate once the window has passed.
        Assert.Equal(JevBudgetDecision.Allowed, New().TryReserve("same"));
    }

    [Fact]
    public void SettlingWithoutAnOutstandingReservationCannotDriveTheLedgerNegative()
    {
        var ledger = New();
        ledger.Settle(0.00005);

        Assert.True(ledger.AllocatedTodayUsd >= 0);
        Assert.Equal(0.00005, ledger.AllocatedTodayUsd, 9);
    }

    [Fact]
    public void ACorruptLedgerDoesNotBlockDictation()
    {
        File.WriteAllText(_path, "{ this is not json");

        // Losing the accounting is bad; losing the user's ability to dictate would be worse.
        var ledger = New();
        Assert.Equal(JevBudgetDecision.Allowed, ledger.TryReserve("after-corruption"));
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
