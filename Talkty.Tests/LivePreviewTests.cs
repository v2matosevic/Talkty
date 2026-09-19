using Talkty.App.Services;
using Xunit;

namespace Talkty.Tests;

/// <summary>
/// Showing the words while he speaks. The rules that matter: one pass at a
/// time, nothing when there is nothing new to hear, and a preview never
/// becomes the text that leaves the app.
/// </summary>
public class LivePreviewTests
{
    private static float[] Audio(double seconds) => new float[(int)(LivePreviewTranscriber.SampleRate * seconds)];

    [Fact]
    public async Task ItPreviewsAsTheRecordingGrows()
    {
        var lengths = new List<int>();
        var texts = new List<string>();
        var clock = 0;
        var audio = Audio(1);

        using var cts = new CancellationTokenSource();
        var preview = new LivePreviewTranscriber(
            () => audio,
            (samples, _) =>
            {
                lengths.Add(samples.Length);
                return Task.FromResult<string?>(lengths.Count == 1 ? "search this" : "search this page for cats");
            },
            texts.Add,
            (_, _) =>
            {
                if (++clock == 1) audio = Audio(1);
                else if (clock == 2) audio = Audio(3);
                else cts.Cancel();
                return Task.CompletedTask;
            });

        await preview.RunAsync(cts.Token);

        Assert.Equal(["search this", "search this page for cats"], texts);
        Assert.Equal(2, preview.Passes);
    }

    [Fact]
    public async Task ItStaysQuietUntilThereIsSomethingToHear()
    {
        var passes = 0;
        var clock = 0;
        using var cts = new CancellationTokenSource();
        var preview = new LivePreviewTranscriber(
            () => Audio(0.3),   // below the minimum, all the way through
            (_, _) => { passes++; return Task.FromResult<string?>("noise"); },
            _ => { },
            (_, _) => { if (++clock >= 3) cts.Cancel(); return Task.CompletedTask; });

        await preview.RunAsync(cts.Token);

        Assert.Equal(0, passes);
        Assert.Equal(0, preview.Passes);
    }

    [Fact]
    public async Task ItDoesNotRepeatItselfWhenNothingChanged()
    {
        var audio = Audio(2);
        var texts = new List<string>();
        var clock = 0;
        using var cts = new CancellationTokenSource();
        var preview = new LivePreviewTranscriber(
            () => audio,
            (_, _) => Task.FromResult<string?>("same words"),
            texts.Add,
            (_, _) =>
            {
                clock++;
                if (clock == 1) audio = Audio(4);          // grew: one pass
                else if (clock == 2) audio = Audio(4.05);  // barely grew: skipped
                else cts.Cancel();
                return Task.CompletedTask;
            });

        await preview.RunAsync(cts.Token);

        Assert.Single(texts);
        Assert.Equal(1, preview.Passes);
    }

    [Fact]
    public async Task LongSpeechOnlyPreviewsTheRecentPart()
    {
        var seen = 0;
        var clock = 0;
        using var cts = new CancellationTokenSource();
        var preview = new LivePreviewTranscriber(
            () => Audio(90),
            (samples, _) => { seen = samples.Length; return Task.FromResult<string?>("a lot of words"); },
            _ => { },
            (_, _) => { if (++clock >= 2) cts.Cancel(); return Task.CompletedTask; });

        await preview.RunAsync(cts.Token);

        Assert.Equal(LivePreviewTranscriber.WindowSamples, seen);
    }

    [Fact]
    public async Task AFailingPreviewStopsInsteadOfFightingTheEngine()
    {
        var passes = 0;
        var clock = 0;
        using var cts = new CancellationTokenSource();
        var preview = new LivePreviewTranscriber(
            () => Audio(2),
            (_, _) => { passes++; throw new InvalidOperationException("engine busy"); },
            _ => Assert.Fail("a failed preview must not reach the pill"),
            (_, _) => { if (++clock >= 5) cts.Cancel(); return Task.CompletedTask; });

        await preview.RunAsync(cts.Token);

        Assert.Equal(1, passes);
    }

    [Fact]
    public async Task CancellationEndsItWithoutThrowing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var preview = new LivePreviewTranscriber(() => Audio(2), (_, _) => Task.FromResult<string?>("x"), _ => Assert.Fail("cancelled"));
        await preview.RunAsync(cts.Token);
        Assert.Equal(0, preview.Passes);
    }
}
