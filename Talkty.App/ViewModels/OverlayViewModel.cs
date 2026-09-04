using CommunityToolkit.Mvvm.ComponentModel;
using System.Diagnostics;

namespace Talkty.App.ViewModels;

public partial class OverlayViewModel : ObservableObject
{
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
