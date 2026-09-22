using System.Diagnostics;
using Talkty.App.Models;

namespace Talkty.App.Services;

/// <summary>
/// Starts the normal, whole-recording decode in a pause. No partial text escapes.
/// Reuse requires sample-for-sample equality with the flushed, trimmed final take.
/// This is scheduling, not phrase splitting or a different recognition model.
/// </summary>
public sealed class SpeculativeTranscriber : IDisposable
{
    private readonly Func<float[]> _snapshot;
    private readonly Func<int, float[]> _tail;
    private readonly Func<float[], CancellationToken, Task<TranscriptionResult>> _transcribe;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _schedule = new();
    private readonly CancellationTokenSource _work;
    private readonly object _lock = new();
    private readonly int _pauseSamples;
    private readonly int _maxPasses;
    private float[]? _candidate;
    private TranscriptionResult? _result;
    private Task? _running;
    private bool _disposed;
    public int Passes { get; private set; }
    public bool Reused { get; private set; }

    public SpeculativeTranscriber(Func<float[]> snapshot, Func<int, float[]> tail,
        Func<float[], CancellationToken, Task<TranscriptionResult>> transcribe,
        bool cloud, CancellationToken cancellationToken,
        Func<int, CancellationToken, Task>? delay = null)
    {
        _snapshot = snapshot;
        _tail = tail;
        _transcribe = transcribe;
        _delay = delay ?? Task.Delay;
        _work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pauseSamples = Constants.SampleRate * (cloud ? 8 : 4) / 10;
        // Bound wasted work: cloud gets at most ONE early pass per recording.
        _maxPasses = cloud ? 1 : 3;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_running != null) throw new InvalidOperationException("Already started.");
            _running = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        var interval = 150;
        using var polling = CancellationTokenSource.CreateLinkedTokenSource(_schedule.Token, _work.Token);
        try
        {
            while (!polling.IsCancellationRequested && Passes < _maxPasses)
            {
                await _delay(interval, polling.Token).ConfigureAwait(false);
                polling.Token.ThrowIfCancellationRequested();
                if (!AudioSilenceTrimmer.IsPause(_tail(_pauseSamples), _pauseSamples)) continue;
                var audio = AudioSilenceTrimmer.Trim(_snapshot());
                if (audio.Length < Constants.SampleRate || AudioSilenceTrimmer.IsQuiet(audio)) continue;
                lock (_lock)
                {
                    if (polling.IsCancellationRequested) return;
                    if (_candidate != null && audio.AsSpan().SequenceEqual(_candidate)) continue;
                    _candidate = audio;
                    _result = null;
                    Passes++;
                }
                var clock = Stopwatch.StartNew();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_work.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(5));
                var result = await _transcribe(audio, attempt.Token).ConfigureAwait(false);
                lock (_lock) _result = result;
                Log.Info($"Early transcription: pass={Passes}, audio={audio.Length / (double)Constants.SampleRate:F1}s, elapsed={clock.ElapsedMilliseconds}ms, success={result.Success}");
                if (!result.Success) return; // Leave retries/recovery to the final pass.
                interval = (int)Math.Clamp(clock.ElapsedMilliseconds * 2, 150, 5000);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Info($"Early transcription unavailable: {ex.Message}"); }
    }

    public void StopScheduling() => _schedule.Cancel();

    public async Task<TranscriptionResult?> CompleteAsync(float[] finalAudio, CancellationToken cancellationToken)
    {
        StopScheduling();
        Task? running;
        lock (_lock)
        {
            running = _running;
            // Don't wait for a stale cloud response or finish obsolete local work.
            if (_candidate == null || !finalAudio.AsSpan().SequenceEqual(_candidate)) _work.Cancel();
        }
        if (running != null) await running.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            Reused = _result is { Success: true } && !string.IsNullOrWhiteSpace(_result.Text)
                && _candidate != null && finalAudio.AsSpan().SequenceEqual(_candidate);
            Log.Info($"Early transcription reuse: {Reused}, passes={Passes}");
            return Reused ? _result : null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _schedule.Cancel();
        _work.Cancel();
        // Never block the dispatcher or dispose cancellation sources beneath a decoder.
        var running = _running ?? Task.CompletedTask;
        _ = running.ContinueWith(_ => { _schedule.Dispose(); _work.Dispose(); }, TaskScheduler.Default);
    }
}
