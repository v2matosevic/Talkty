using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Talkty.App.Services;

/// <summary>
/// One durable line of the fidelity comparison record. Deliberately holds NO dictation or prompt
/// text — only hashes, lengths, labels, status and usage. The record exists to compare the model
/// layer against the existing length guard over real use, not to build a transcript archive.
/// </summary>
public sealed class JevFidelityRecord
{
    public DateTime TimestampUtc { get; set; }
    public string Mode { get; set; } = "";

    public string TranscriptHash { get; set; } = "";
    public int TranscriptLength { get; set; }
    public string RewriteHash { get; set; } = "";
    public int RewriteLength { get; set; }

    /// <summary>What the existing PromptRefinementService.IsSuspectedSummary baseline said.</summary>
    public bool BaselineGuardTripped { get; set; }

    /// <summary>Exact, model-free findings (missing identifier/number, dropped prohibition).</summary>
    public int CodeConcerns { get; set; }

    public string JevStatus { get; set; } = "";
    public string? FailureCode { get; set; }
    public string? Model { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }

    /// <summary>Null means the provider reported no cost. Never written as zero.</summary>
    public double? CostUsd { get; set; }
    public long? ElapsedMs { get; set; }

    /// <summary>Concern kinds plus clause IDs, e.g. <c>OmittedClause:c3</c>. No user text.</summary>
    public List<string> Concerns { get; set; } = new();

    /// <summary>How many concerns were actually shown to the user (always 0 in Record only).</summary>
    public int Surfaced { get; set; }
}

/// <summary>Why a request was not sent.</summary>
public enum JevBudgetDecision { Allowed, DailyBudgetExhausted, RateLimited, Duplicate }

/// <summary>
/// Local spending reservation, rate limit and comparison record for the fidelity check, in
/// <c>%AppData%/Talkty/jev-fidelity.json</c>.
///
/// The reservation is taken BEFORE the request and replaced by the reported cost afterwards. A
/// crash therefore leaves a reservation standing rather than letting the spend disappear, and an
/// unreported cost keeps its reservation instead of being recorded as free. This is a local
/// reservation limit at the researched price — it is not an OpenRouter account credit limit and
/// not a guarantee against a provider price change.
/// </summary>
public sealed class JevFidelityLedger
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _lock = new();
    private readonly string _path;
    private State _state = new();
    private bool _loaded;

    public JevFidelityLedger(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Talkty", "jev-fidelity.json");
    }

    private sealed class State
    {
        public string Day { get; set; } = "";
        /// <summary>Reserved-but-unsettled spend for today, in millionths of a dollar.</summary>
        public long ReservedMicroUsd { get; set; }
        /// <summary>Settled spend for today, in millionths of a dollar.</summary>
        public long SpentMicroUsd { get; set; }
        public List<long> Attempts { get; set; } = new();
        public List<string> RecentHashes { get; set; } = new();
        public List<long> RecentHashTimes { get; set; } = new();
        public List<JevFidelityRecord> Records { get; set; } = new();
    }

    /// <summary>Today's committed plus reserved spend, in US dollars.</summary>
    public double AllocatedTodayUsd
    {
        get { lock (_lock) { Load(); RollDay(); return (_state.ReservedMicroUsd + _state.SpentMicroUsd) / 1_000_000d; } }
    }

    /// <summary>Records currently retained (newest last).</summary>
    public IReadOnlyList<JevFidelityRecord> Records
    {
        get { lock (_lock) { Load(); return _state.Records.ToList(); } }
    }

    /// <summary>
    /// Takes a reservation for one attempt, or explains why no request may be sent.
    /// </summary>
    public JevBudgetDecision TryReserve(string inputHash)
    {
        lock (_lock)
        {
            Load();
            RollDay();

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            _state.Attempts.RemoveAll(t => now - t > 60);
            if (_state.Attempts.Count >= Constants.JevMaxAttemptsPerMinute)
                return JevBudgetDecision.RateLimited;

            // Drop expired duplicate-suppression entries, then reject a repeat of the same work.
            for (int i = _state.RecentHashTimes.Count - 1; i >= 0; i--)
            {
                if (now - _state.RecentHashTimes[i] <= Constants.JevDuplicateSuppressionSeconds) continue;
                _state.RecentHashTimes.RemoveAt(i);
                if (i < _state.RecentHashes.Count) _state.RecentHashes.RemoveAt(i);
            }
            if (_state.RecentHashes.Contains(inputHash, StringComparer.Ordinal))
                return JevBudgetDecision.Duplicate;

            if (_state.ReservedMicroUsd + _state.SpentMicroUsd + Constants.JevReservationMicroUsd
                > Constants.JevDailyBudgetMicroUsd)
                return JevBudgetDecision.DailyBudgetExhausted;

            _state.ReservedMicroUsd += Constants.JevReservationMicroUsd;
            _state.Attempts.Add(now);
            _state.RecentHashes.Add(inputHash);
            _state.RecentHashTimes.Add(now);
            Save();
            return JevBudgetDecision.Allowed;
        }
    }

    /// <summary>
    /// Settles one reservation. A known cost replaces it (rounded UP to whole microdollars, so
    /// accounting never under-counts); an unknown cost leaves the reservation standing.
    /// </summary>
    public void Settle(double? costUsd)
    {
        lock (_lock)
        {
            Load();
            if (costUsd is not { } cost)
                return; // Unknown charge: the conservative reservation stays allocated.

            _state.ReservedMicroUsd = Math.Max(0, _state.ReservedMicroUsd - Constants.JevReservationMicroUsd);
            _state.SpentMicroUsd += (long)Math.Ceiling(Math.Max(0, cost) * 1_000_000d);
            Save();
        }
    }

    /// <summary>Appends one comparison record, trimming the oldest beyond the retention cap.</summary>
    public void Append(JevFidelityRecord record)
    {
        lock (_lock)
        {
            Load();
            _state.Records.Add(record);
            if (_state.Records.Count > Constants.JevMaxRecords)
                _state.Records.RemoveRange(0, _state.Records.Count - Constants.JevMaxRecords);
            Save();
        }
    }

    private void RollDay()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_state.Day == today) return;
        _state.Day = today;
        _state.ReservedMicroUsd = 0;
        _state.SpentMicroUsd = 0;
        _state.Attempts.Clear();
    }

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(_path))
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? new State();
        }
        catch (Exception ex)
        {
            // A corrupt ledger must not block dictation; start a fresh day's accounting.
            Log.Warning($"Jev ledger unreadable, starting fresh: {ex.Message}");
            _state = new State();
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Atomic write (temp + move), same as settings/history.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_state, Options));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warning($"Jev ledger save failed: {ex.Message}");
        }
    }
}
