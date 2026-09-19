namespace Talkty.App.Services;

/// <summary>
/// Shows the words while he is still speaking.
///
/// Whisper only answers when it has audio, so "real time" here means repeatedly
/// transcribing what has been captured so far and putting that on the pill. It
/// is a preview and nothing else: the text that leaves this app is always the
/// final pass over the whole recording, never one of these.
///
/// Two rules keep it from costing anything that matters:
/// one pass at a time, and never while there is nothing new to hear. The engine
/// holds a single native decode state, so a preview and the real transcription
/// can never run at once; a preview in flight is awaited, never cancelled
/// mid-decode, because that is what wedges the decoder.
/// </summary>
public sealed class LivePreviewTranscriber
{
    private readonly Func<float[]> _samples;
    private readonly Func<float[], CancellationToken, Task<string?>> _transcribe;
    private readonly Action<string> _onPreview;
    private readonly Func<int, CancellationToken, Task> _delay;

    /// <summary>16 kHz mono: a second of speech is 16000 samples.</summary>
    public const int SampleRate = 16_000;

    /// <summary>Below this there is nothing worth showing, and Whisper hallucinates.</summary>
    public const int MinimumSamples = SampleRate * 4 / 5;

    /// <summary>Growth needed before another pass: about a third of a second of new speech.</summary>
    public const int MinimumGrowth = SampleRate / 3;

    /// <summary>Long dictation stays cheap by previewing only the recent audio.</summary>
    public const int WindowSamples = SampleRate * 25;

    public int IntervalMs { get; init; } = 900;

    public LivePreviewTranscriber(
        Func<float[]> samples,
        Func<float[], CancellationToken, Task<string?>> transcribe,
        Action<string> onPreview,
        Func<int, CancellationToken, Task>? delay = null)
    {
        _samples = samples;
        _transcribe = transcribe;
        _onPreview = onPreview;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>How many passes ran. Useful for proving it does not spin.</summary>
    public int Passes { get; private set; }

    /// <summary>
    /// Preview until cancelled. Never throws: a preview failing is not a reason
    /// for a recording to fail, and the final transcription is untouched by it.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var previous = 0;
        var lastText = string.Empty;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _delay(IntervalMs, cancellationToken);
                if (cancellationToken.IsCancellationRequested) return;

                var audio = _samples();
                if (audio.Length < MinimumSamples || audio.Length - previous < MinimumGrowth) continue;
                previous = audio.Length;

                var window = audio.Length > WindowSamples ? audio[^WindowSamples..] : audio;
                Passes++;
                var text = await _transcribe(window, cancellationToken);
                if (cancellationToken.IsCancellationRequested) return;

                text = text?.Trim();
                if (string.IsNullOrEmpty(text) || text == lastText) continue;
                lastText = text;
                _onPreview(text);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // A preview is a nicety. If the engine is busy or unhappy, stop
                // previewing and let the real transcription do its job.
                Log.Info($"Live preview stopped: {ex.Message}");
                return;
            }
        }
    }
}
