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

public record VoiceCommandResult(VoiceCommandOutcome Outcome, string Message, string? CommandId = null)
{
    public bool Delivered => Outcome == VoiceCommandOutcome.Delivered;
}

public interface IVoiceCommandService
{
    /// <summary>True when an endpoint and a token are configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Hand one spoken instruction to the local command daemon. Never throws:
    /// a failure comes back as <see cref="VoiceCommandOutcome.NotReached"/> so the
    /// caller can fall back to ordinary dictation and the sentence is not lost.
    /// </summary>
    Task<VoiceCommandResult> DispatchAsync(
        string text,
        string? foregroundApp,
        CancellationToken cancellationToken);
}
