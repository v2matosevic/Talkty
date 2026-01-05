namespace Talkty.App.Services;

/// <summary>
/// Service for temporarily lowering system volume during recording
/// to prevent audio interference from system sounds.
/// </summary>
public interface IVolumeDuckingService : IDisposable
{
    /// <summary>
    /// Smoothly reduces system volume by the configured duck level.
    /// </summary>
    Task DuckAsync();

    /// <summary>
    /// Smoothly restores system volume to the level before ducking.
    /// </summary>
    Task RestoreAsync();

    /// <summary>
    /// Indicates whether volume is currently ducked.
    /// </summary>
    bool IsDucked { get; }

    /// <summary>
    /// The level to duck volume to (0.05 to 1.0). Lower = quieter.
    /// Default is 0.20 (20% of original volume).
    /// </summary>
    float DuckLevel { get; set; }
}
