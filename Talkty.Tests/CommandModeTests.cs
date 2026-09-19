using Talkty.App.Models;
using Talkty.App.Services;
using Xunit;

namespace Talkty.Tests;

/// <summary>
/// Command mode (Alt+W): the same recording, a different destination. The rules that
/// matter are that a command never reaches the clipboard or the cursor, that Prompting
/// never rewrites an instruction, and that a sentence is never lost when the daemon is
/// not there.
/// </summary>
public partial class TranscriptionFlowTests
{
    [Fact]
    public Task CommandModeSendsTheTranscriptAndNeverTouchesTheClipboard() => ui.Run(async () =>
    {
        using var context = new Context();
        context.ViewModel.MarkRecordingAsCommand();
        await context.Record();

        Assert.Single(context.Voice.Sent);
        // Post-processing still applies (it adds the closing period); the point is that
        // what the daemon receives is the transcript, not a rewrite of it.
        Assert.StartsWith(context.Engine.Text, context.Voice.Sent[0]);
        Assert.Equal("Original clipboard", context.Clipboard.Text);
        Assert.Empty(context.Clipboard.Writes);
        Assert.Equal(0, context.Paste.Pasted);
    });

    [Fact]
    public Task ACommandStillLandsInHistory() => ui.Run(async () =>
    {
        using var context = new Context();
        context.ViewModel.MarkRecordingAsCommand();
        await context.Record();

        Assert.Single(context.ViewModel.History);
        Assert.StartsWith(context.Engine.Text, context.ViewModel.History[0].Text);
    });

    [Fact]
    public Task TheDaemonsAnswerBecomesTheStatus() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Voice.Result = new VoiceCommandResult(VoiceCommandOutcome.Delivered, "opened https://youtube.com");
        context.ViewModel.MarkRecordingAsCommand();
        await context.Record();

        Assert.Contains("opened https://youtube.com", context.Warnings);
    });

    [Fact]
    public Task AnUnreachableDaemonFallsBackToOrdinaryDictation() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Voice.Result = new VoiceCommandResult(VoiceCommandOutcome.NotReached, "No command daemon is listening");
        context.ViewModel.MarkRecordingAsCommand();
        await context.Record();

        // The sentence is not lost: it goes where dictation would have put it.
        Assert.Single(context.Voice.Sent);
        Assert.NotEmpty(context.Clipboard.Writes);
        Assert.Contains(context.Warnings, w => w.Contains("No command daemon is listening"));
    });

    [Fact]
    public Task AnUncertainOutcomeDoesNotFallBack() => ui.Run(async () =>
    {
        using var context = new Context();
        // It may already have run. Pasting the words now would be a second action.
        context.Voice.Result = new VoiceCommandResult(VoiceCommandOutcome.Uncertain, "No answer from the command daemon");
        context.ViewModel.MarkRecordingAsCommand();
        await context.Record();

        Assert.Empty(context.Clipboard.Writes);
        Assert.Equal(0, context.Paste.Pasted);
        Assert.Contains(context.Warnings, w => w.Contains("No answer"));
    });

    [Fact]
    public Task AnUnconfiguredCommandModeFallsBackWithoutSending() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Voice.IsConfigured = false;
        context.ViewModel.MarkRecordingAsCommand();
        await context.Record();

        Assert.Empty(context.Voice.Sent);
        Assert.NotEmpty(context.Clipboard.Writes);
    });

    [Fact]
    public Task PromptingNeverRewritesACommand() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Refiner.Run = _ => Task.FromResult<string?>("A long structured coding prompt");
        context.ViewModel.MarkRecordingAsCommand();
        await context.ViewModel.StartListeningAsync();
        context.ViewModel.PromptMode = true;
        await context.ViewModel.StopListeningAndTranscribeAsync();

        Assert.Single(context.Voice.Sent);
        Assert.StartsWith(context.Engine.Text, context.Voice.Sent[0]);
        Assert.DoesNotContain("A long structured coding prompt", context.Voice.Sent);
    });

    [Fact]
    public Task AnOrdinaryDictationNeverReachesTheDaemon() => ui.Run(async () =>
    {
        using var context = new Context();
        await context.Record();

        Assert.Empty(context.Voice.Sent);
        Assert.NotEmpty(context.Clipboard.Writes);
    });

    [Fact]
    public Task TheModeIsConsumedBySingleRecordingAndDoesNotLeak() => ui.Run(async () =>
    {
        using var context = new Context();
        context.ViewModel.MarkRecordingAsCommand();
        await context.Record();
        Assert.Single(context.Voice.Sent);

        // The next recording is ordinary dictation again.
        await context.Record();
        Assert.Single(context.Voice.Sent);
        Assert.NotEmpty(context.Clipboard.Writes);
    });

    [Fact]
    public Task ARefusedStartDoesNotLeaveCommandModeArmed() => ui.Run(() =>
    {
        using var context = new Context();
        // No model is loaded in a fresh view model, so ToggleListening refuses. The
        // arming must not survive to poison the next ordinary dictation.
        Assert.False(context.ViewModel.IsModelLoaded);
        context.ViewModel.ToggleCommandListening();
        Assert.False(context.ViewModel.IsCommandModeArmed);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ARecoveryReplayIsNeverTreatedAsACommand() => ui.Run(async () =>
    {
        using var context = new Context();
        context.ViewModel.MarkRecordingAsCommand();
        var recording = new RecoverableRecording { Samples = [0.25f, -0.5f] };
        await context.ViewModel.StopListeningAndTranscribeAsync(recording);

        Assert.Empty(context.Voice.Sent);
    });
}
