namespace Talkty.App.Models;

/// <summary>
/// Event arguments for toast notification requests.
/// </summary>
public class ToastEventArgs : EventArgs
{
    /// <summary>
    /// The message to display in the toast.
    /// </summary>
    public string Message { get; init; } = "";

    /// <summary>
    /// The visual style of the toast.
    /// </summary>
    public ToastType Type { get; init; } = ToastType.Info;

    /// <summary>
    /// How long the toast remains visible, in milliseconds.
    /// </summary>
    public int DurationMs { get; init; } = 3000;
}
