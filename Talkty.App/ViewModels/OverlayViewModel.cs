using CommunityToolkit.Mvvm.ComponentModel;
using System.Diagnostics;

namespace Talkty.App.ViewModels;

/// <summary>
/// What the pill is showing about a spoken command. The point is that he can
/// watch it happen: his words, then what the machine is doing, then what it
/// actually did or why it did not.
/// </summary>
public enum CommandStage
{
    /// <summary>Not a command; the pill behaves as it always has.</summary>
    None,

    /// <summary>His words are on their way to the daemon.</summary>
    Sending,

    /// <summary>The daemon took it and is still working.</summary>
    Working,

    /// <summary>It ran, and the daemon said what happened.</summary>
    Succeeded,

    /// <summary>It did not run, stopped early, or could not be verified.</summary>
    Failed,
}

public partial class OverlayViewModel : ObservableObject
{
    /// <summary>The spoken command, shown so he can see what was heard.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCommand))]
    private string _commandText = string.Empty;

    /// <summary>The daemon's own words about it: what ran, or why nothing did.</summary>
    [ObservableProperty]
    private string _commandDetail = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCommand))]
    [NotifyPropertyChangedFor(nameof(IsCommandBusy))]
    [NotifyPropertyChangedFor(nameof(IsCommandFailed))]
    [NotifyPropertyChangedFor(nameof(IsCommandDone))]
    private CommandStage _commandStage = CommandStage.None;

    public bool IsCommand => CommandStage != CommandStage.None;
    public bool IsCommandBusy => CommandStage is CommandStage.Sending or CommandStage.Working;
    public bool IsCommandFailed => CommandStage == CommandStage.Failed;
    public bool IsCommandDone => CommandStage == CommandStage.Succeeded;

    /// <summary>Back to an ordinary recording pill.</summary>
    public void ClearCommand()
    {
        CommandStage = CommandStage.None;
        CommandText = string.Empty;
        CommandDetail = string.Empty;
    }

    [ObservableProperty]
    private string _statusText = "Listening...";

    [ObservableProperty]
    private float _audioLevel;

    [ObservableProperty]
    private bool _isListening;

    [ObservableProperty]
    private bool _isTranscribing;

    [ObservableProperty]
    private string _elapsedTime = "00:00";

    /// <summary>
    /// When true, this recording is treated as an AI-agent prompt: the transcription is expanded
    /// into a structured prompt before output. Toggled via the "Prompting" button on the pill.
    /// </summary>
    [ObservableProperty]
    private bool _isPromptMode;

    private readonly Stopwatch _elapsed = new();
    private System.Windows.Threading.DispatcherTimer? _timer;

    public void StartTimer()
    {
        StopTimer();
        ElapsedTime = "00:00";
        AudioLevel = 0;
        _elapsed.Restart();
        _timer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _timer.Tick -= OnTimerTick;
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    public void StopTimer()
    {
        _timer?.Stop();
        _elapsed.Stop();
    }

    private void OnTimerTick(object? sender, EventArgs e) => ElapsedTime = FormatElapsed(_elapsed.Elapsed);

    internal static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
}
