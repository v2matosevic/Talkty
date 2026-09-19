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

    /// <summary>
    /// Only the recent audio is previewed. A long recording would otherwise
    /// re-transcribe the whole thing every second, which is what made the app
    /// feel laggy: the preview is a glance at what is being said, not a record.
    /// </summary>
    public const int WindowSamples = SampleRate * 8;

    /// <summary>The soonest another pass may start.</summary>
    public int IntervalMs { get; init; } = 900;

    /// <summary>The latest, once the machine has shown it is busy.</summary>
    public int MaxIntervalMs { get; init; } = 4_000;

    private readonly Func<DateTime> _now;

    public LivePreviewTranscriber(
        Func<float[]> samples,
        Func<float[], CancellationToken, Task<string?>> transcribe,
        Action<string> onPreview,
        Func<int, CancellationToken, Task>? delay = null,
        Func<DateTime>? now = null)
    {
        _samples = samples;
        _transcribe = transcribe;
        _onPreview = onPreview;
        _delay = delay ?? Task.Delay;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>The gap it settled on, after watching how long a pass takes.</summary>
    public int CurrentIntervalMs { get; private set; }

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
        CurrentIntervalMs = IntervalMs;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _delay(CurrentIntervalMs, cancellationToken);
                if (cancellationToken.IsCancellationRequested) return;

                var audio = _samples();
                if (audio.Length < MinimumSamples || audio.Length - previous < MinimumGrowth) continue;
                previous = audio.Length;

                var window = audio.Length > WindowSamples ? audio[^WindowSamples..] : audio;
                Passes++;
                var started = _now();
                var text = await _transcribe(window, cancellationToken);
                // A busy machine says so by being slow. Back off to twice what a
                // pass costs, so previewing never crowds out the real work.
                var took = (int)(_now() - started).TotalMilliseconds;
                CurrentIntervalMs = Math.Clamp(took * 2, IntervalMs, MaxIntervalMs);
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
