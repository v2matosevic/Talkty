using Talkty.App.ViewModels;

namespace Talkty.App.Models;

/// <summary>
/// One step in the life of a spoken command, for the pill to show: his words,
/// then what the machine is doing, then what it did or why it did not.
/// </summary>
public class CommandProgressEventArgs : EventArgs
{
    /// <summary>The command as it was heard.</summary>
    public string Text { get; init; } = "";

    /// <summary>Where it has got to. <see cref="CommandStage.None"/> clears the pill.</summary>
    public CommandStage Stage { get; init; } = CommandStage.None;

    /// <summary>The daemon's own words about it, never this app's invention.</summary>
    public string Detail { get; init; } = "";
}
