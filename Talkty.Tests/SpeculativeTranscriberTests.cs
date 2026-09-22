using Talkty.App;
using Talkty.App.Models;
using Talkty.App.Services;
using Xunit;

namespace Talkty.Tests;

public class SpeculativeTranscriberTests
{
    internal static float[] Take(int silence = 16000) =>
        [.. Enumerable.Repeat(0.1f, 32000), .. new float[silence]];
    private static TranscriptionResult Success(string text = "Keep every final word.") => new() { Success = true, Text = text };

    [Theory]
    [InlineData(1)]
    [InlineData(1599)]
    [InlineData(23017)]
    public void AdditionalSilenceDoesNotChangeTheAudioSentToTheModel(int extra)
    {
        Assert.Equal(AudioSilenceTrimmer.Trim(Take()), AudioSilenceTrimmer.Trim(Take(16000 + extra)));
    }

    [Fact]
    public void AQuietFinalWordAndPartialCaptureBufferRemainInTheFullTake()
    {
        var audio = Take();
        var final = audio.Concat(Enumerable.Repeat(0.025f, 513)).ToArray();
        Assert.Equal(final, AudioSilenceTrimmer.Trim(final));
        Assert.NotEqual(AudioSilenceTrimmer.Trim(audio), AudioSilenceTrimmer.Trim(final));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdenticalWholeTakeIsReusedWithoutAnotherDecode(bool cloud)
    {
        var audio = Take();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var early = new SpeculativeTranscriber(() => audio, n => audio[^n..], (samples, _) =>
        {
            Assert.Equal(AudioSilenceTrimmer.Trim(audio), samples);
            calls++;
            finished.TrySetResult();
            return Task.FromResult(Success());
        }, cloud, default, (_, ct) => Task.Delay(1, ct));
        early.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var result = await early.CompleteAsync(AudioSilenceTrimmer.Trim(Take(29013)), default);
        Assert.Equal("Keep every final word.", result?.Text);
        Assert.True(early.Reused);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OneChangedSampleRejectsAnOtherwiseMatchingResult()
    {
        var audio = Take();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var early = new SpeculativeTranscriber(() => audio, n => audio[^n..], (_, _) =>
        {
            entered.TrySetResult();
            return Task.FromResult(Success());
        }, true, default, (_, ct) => Task.Delay(1, ct));
        early.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var final = AudioSilenceTrimmer.Trim(audio);
        final[100] += 0.00001f;
        Assert.Null(await early.CompleteAsync(final, default));
    }

    [Fact]
    public async Task NewSpeechCancelsStaleWorkAndWaitsForItToUnwind()
    {
        var audio = Take();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unwound = false;
        using var early = new SpeculativeTranscriber(() => audio, n => audio[^n..], async (_, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { unwound = true; }
            return Success();
        }, false, default, (_, ct) => Task.Delay(1, ct));
        early.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var final = audio.Concat(Enumerable.Repeat(0.1f, 3200)).ToArray();
        Assert.Null(await early.CompleteAsync(AudioSilenceTrimmer.Trim(final), default).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(unwound);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyFailureOrEmptyTextLeavesTheFinalPassResponsible(bool empty)
    {
        var audio = Take();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var early = new SpeculativeTranscriber(() => audio, n => audio[^n..], (_, _) =>
        {
            entered.TrySetResult();
            return Task.FromResult(empty ? Success("") : new TranscriptionResult { ErrorMessage = "Busy" });
        }, true, default, (_, ct) => Task.Delay(1, ct));
        early.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(await early.CompleteAsync(AudioSilenceTrimmer.Trim(audio), default));
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public async Task ExtraWorkIsBoundedEvenIfTheUserKeepsPausing(bool cloud, int limit)
    {
        var audio = Take();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var early = new SpeculativeTranscriber(() => audio, n => audio[^n..], (_, _) =>
        {
            audio = [.. audio, .. Enumerable.Repeat(0.1f, 32000), .. new float[16000]];
            if (++calls == limit) entered.TrySetResult();
            return Task.FromResult(Success());
        }, cloud, default, (_, ct) => Task.Delay(1, ct));
        early.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(await early.CompleteAsync(AudioSilenceTrimmer.Trim(audio), default));
        Assert.Equal(limit, calls);
    }

    [Fact]
    public async Task ContinuousSpeechAndSilenceAloneNeverStartEarlyRecognition()
    {
        var audio = Enumerable.Repeat(0.1f, 32000).ToArray();
        var ticks = 0;
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var early = new SpeculativeTranscriber(() => audio, n => audio[^n..], (_, _) =>
            throw new Exception("Must not decode"), false, default, async (_, ct) =>
        {
            if (++ticks == 3) audio = new float[32000];
            if (ticks == 6) observed.TrySetResult();
            await Task.Delay(1, ct);
        });
        early.Start();
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(await early.CompleteAsync(audio, default));
        Assert.Equal(0, early.Passes);
    }

    [Fact]
    public async Task CancellationDiscardsEvenACompletedMatchingResult()
    {
        var audio = Take();
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var early = new SpeculativeTranscriber(() => audio, n => audio[^n..], (_, _) =>
        {
            entered.TrySetResult();
            return Task.FromResult(Success());
        }, true, cts.Token, (_, ct) => Task.Delay(1, ct));
        early.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => early.CompleteAsync(AudioSilenceTrimmer.Trim(audio), cts.Token));
    }
}
