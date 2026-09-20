namespace Talkty.App.Services;

/// <summary>
/// What happened when a command-mode dictation was handed to the local daemon.
/// The three cases are deliberately distinct because they need different recovery:
/// only <see cref="NotReached"/> is safe to treat as an ordinary dictation.
/// </summary>
public enum VoiceCommandOutcome
{
    /// <summary>The daemon answered. Whatever it did, it owns the result.</summary>
    Delivered,

    /// <summary>Nothing was listening, or it refused us. The command certainly did not run.</summary>
    NotReached,

    /// <summary>We asked and never heard back. It MAY have run; never send it again.</summary>
    Uncertain,
}

public record VoiceCommandResult(
    VoiceCommandOutcome Outcome,
    string Message,
    string? CommandId = null,
    bool Ok = false,
    string? GoalId = null)
{
    public bool Delivered => Outcome == VoiceCommandOutcome.Delivered;

    /// <summary>The daemon took the goal but has not finished it; its result arrives later.</summary>
    public bool IsWorking => GoalId is not null;
}

/// <summary>One reading of a goal the daemon is still working on.</summary>
public record VoiceGoalUpdate(string Status, string? Detail, int Steps = 0)
{
    private static readonly HashSet<string> Waiting = new(StringComparer.OrdinalIgnoreCase)
        { "running", "cancelling", "queued" };

    /// <summary>True once nothing more will happen without the owner saying something.</summary>
    public bool Finished => !Waiting.Contains(Status);

    /// <summary>Only "done" is a success; the rest are stopped, refused or unverified.</summary>
    public bool Succeeded => string.Equals(Status, "done", StringComparison.OrdinalIgnoreCase);
}

public interface IVoiceCommandService
{
    /// <summary>True when an endpoint and a token are configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Whether a daemon has announced itself and its process is still alive.
    /// Cheap: it reads the service record, and never opens a connection.
    /// </summary>
    bool IsDaemonLive => true;

    /// <summary>
    /// Hand one spoken instruction to the local command daemon. Never throws:
    /// a failure comes back as <see cref="VoiceCommandOutcome.NotReached"/> so the
    /// caller can fall back to ordinary dictation and the sentence is not lost.
    /// </summary>
    Task<VoiceCommandResult> DispatchAsync(
        string text,
        string? foregroundApp,
        CancellationToken cancellationToken);

    Task<VoiceCommandResult> DispatchAsync(
        string text,
        string? foregroundApp,
        CancellationToken cancellationToken,
        CapturedWindowInfo? targetWindow)
        => DispatchAsync(text, foregroundApp, cancellationToken);

    /// <summary>
    /// Watch a goal the daemon accepted but has not finished, reporting each
    /// change until it ends. Never throws: an unreachable daemon simply stops
    /// the watch, because the goal's own result is not this app's to invent.
    /// </summary>
    Task FollowGoalAsync(
        string goalId,
        IProgress<VoiceGoalUpdate> progress,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}
