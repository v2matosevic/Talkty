using CommunityToolkit.Mvvm.ComponentModel;

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

    private DateTime _startTime;
    private System.Windows.Threading.DispatcherTimer? _timer;

    public void StartTimer()
    {
        _startTime = DateTime.Now;
        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _timer.Tick += (s, e) =>
        {
            var elapsed = DateTime.Now - _startTime;
            ElapsedTime = elapsed.ToString(@"mm\:ss");
        };
        _timer.Start();
    }

    public void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }
}
