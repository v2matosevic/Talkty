using Talkty.App.Models;
using Xunit;

namespace Talkty.Tests;

public partial class TranscriptionFlowTests
{
    [Fact]
    public Task StoppingACommandDoesNotWaitOnItsCancelledPreviewDispatcher() => ui.Run(async () =>
    {
        using var context = new Context();
        context.ViewModel.MarkRecordingAsCommand();
        await context.ViewModel.StartListeningAsync();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await context.ViewModel.StopListeningAndTranscribeAsync();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"Stop blocked for {clock.Elapsed}");
        Assert.Single(context.Voice.Sent);
    });

    [Fact]
    public Task PausedTakeIsPastedOnceOnlyAfterStop() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Audio.Samples = SpeculativeTranscriberTests.Take();
        var earlyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Engine.Run = (_, callback) =>
        {
            Assert.Null(callback);
            earlyStarted.TrySetResult();
            return Task.FromResult(new TranscriptionResult { Success = true, Text = "The complete recording" });
        };
        await context.ViewModel.StartListeningAsync();
        await earlyStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, context.Paste.Pasted);
        Assert.Empty(context.Clipboard.Writes);
        context.Audio.Samples = SpeculativeTranscriberTests.Take(23741);
        await context.ViewModel.StopListeningAndTranscribeAsync();
        Assert.Equal(1, context.Engine.Calls);
        Assert.Equal(1, context.Paste.Pasted);
        Assert.Contains("The complete recording", context.Clipboard.Text);
    });

    [Fact]
    public Task SpeechArrivingInTheFinalMicrophoneBufferForcesACompleteFreshPass() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Audio.Samples = SpeculativeTranscriberTests.Take();
        var earlyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Engine.Run = (_, _) =>
        {
            earlyStarted.TrySetResult();
            return Task.FromResult(new TranscriptionResult
            {
                Success = true, Text = context.Engine.Calls == 1 ? "Incomplete" : "Including the final word"
            });
        };
        context.Audio.Flush = () =>
        {
            context.Audio.Samples = [.. context.Audio.Samples, .. Enumerable.Repeat(0.1f, 4000)];
            return Task.FromResult(true);
        };
        await context.ViewModel.StartListeningAsync();
        await earlyStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await context.ViewModel.StopListeningAndTranscribeAsync();
        Assert.Equal(2, context.Engine.Calls);
        Assert.Contains("Including the final word", context.Clipboard.Text);
        Assert.Equal(1, context.Paste.Pasted);
    });

    [Fact]
    public Task DisablingEarlyRecognitionPersistsAndKeepsTheNormalSinglePass() => ui.Run(async () =>
    {
        using var context = new Context();
        context.ViewModel.ApplySettings(new AppSettings { TranscribeDuringPauses = false, AutoPaste = true });
        Assert.False(context.Settings.Settings.TranscribeDuringPauses);
        context.Audio.Samples = SpeculativeTranscriberTests.Take();
        await context.ViewModel.StartListeningAsync();
        await Task.Delay(250);
        Assert.Equal(0, context.Engine.Calls);
        await context.ViewModel.StopListeningAndTranscribeAsync();
        Assert.Equal(1, context.Engine.Calls);
    });
}
