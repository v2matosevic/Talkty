using System.Linq;
using Talkty.App.Models;
using Talkty.App.Services;
using Talkty.App.ViewModels;
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
    public Task ACommandKeepsTheWindowCapturedBeforeTranscription() => ui.Run(async () =>
    {
        using var context = new Context();
        var original = new CapturedWindowInfo("101", 1, "brave", "Original page", "fixture");
        context.Paste.CapturedWindow = original;
        context.Engine.Run = (_, _) =>
        {
            context.Paste.CapturedWindow = new CapturedWindowInfo("202", 2, "Code", "Later editor", "fixture");
            return Task.FromResult(new TranscriptionResult { Success = true, Text = "search this" });
        };
        context.ViewModel.MarkRecordingAsCommand();
        await context.Record();
        Assert.Same(original, context.Voice.Target);
        Assert.Empty(context.Clipboard.Writes);
    });

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
    public Task TheSpokenWordsAndTheDaemonsAnswerBothReachThePill() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Voice.Result = new VoiceCommandResult(VoiceCommandOutcome.Delivered, "opened https://youtube.com", Ok: true);
        context.ViewModel.MarkRecordingAsCommand();
        await context.Record();

        // His words go up before the daemon answers, so the wait reads as his.
        Assert.Equal(CommandStage.Sending, context.Pill[0].Stage);
        Assert.StartsWith(context.Engine.Text, context.Pill[0].Text);

        var final = context.Pill.Last();
        Assert.Equal(CommandStage.Succeeded, final.Stage);
        Assert.Equal("opened https://youtube.com", final.Detail);
        Assert.StartsWith(context.Engine.Text, final.Text);
    });

    [Fact]
    public Task AFailedCommandSaysSoOnThePillInsteadOfLookingDone() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Voice.Result = new VoiceCommandResult(VoiceCommandOutcome.Delivered,
            "The window you were using is not open any more.", Ok: false);
        context.ViewModel.MarkRecordingAsCommand();
        await context.Record();

        var final = context.Pill.Last();
        Assert.Equal(CommandStage.Failed, final.Stage);
        Assert.Contains("not open any more", final.Detail);
    });

    [Fact]
    public Task AnAcceptedGoalKeepsThePillWorkingRatherThanClaimingSuccess() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Voice.Result = new VoiceCommandResult(VoiceCommandOutcome.Delivered,
            "Working on the goal.", Ok: true, GoalId: "g-7");
        context.ViewModel.MarkRecordingAsCommand();
        await context.Record();

        var final = context.Pill.Last();
        Assert.Equal(CommandStage.Working, final.Stage);
        // Following is deliberately detached, so the next Alt+W never waits on
        // a goal; give that task a moment to pick it up.
        for (var i = 0; i < 40 && context.Voice.Followed is null; i++) await Task.Delay(25);
        Assert.Equal("g-7", context.Voice.Followed);
    });

    [Fact]
    public Task AnUnreachableDaemonNeverTurnsACommandIntoTypedText() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Voice.IsDaemonLive = false;
        context.Voice.Result = new VoiceCommandResult(VoiceCommandOutcome.NotReached, "No command daemon is listening");
        context.ViewModel.MarkRecordingAsCommand();
        await context.Record();

        // Alt+W is an instruction. With nothing to run it, nothing runs — and
        // above all nothing is typed into whatever he was looking at.
        Assert.Empty(context.Clipboard.Writes);
        Assert.Equal(0, context.Paste.Pasted);
        Assert.Equal(CommandStage.Failed, context.Pill.Last().Stage);
        Assert.Contains("nothing ran", context.Pill.Last().Detail);
        Assert.Contains(context.Warnings, w => w.Contains("not typed anywhere"));
        // The words are still recoverable.
        Assert.Single(context.ViewModel.History);
    });

    [Fact]
    public Task AMissingDaemonIsStartedAndTheCommandRunsOnce() => ui.Run(async () =>
    {
        using var context = new Context();
        context.ShimPresent = true;
        context.Voice.IsDaemonLive = false;
        context.Voice.Result = new VoiceCommandResult(VoiceCommandOutcome.NotReached, "No command daemon is listening");
        // The daemon finishes booting just after the first attempt fails.
        context.Voice.AfterDispatch = voice =>
        {
            voice.AfterDispatch = null;
            voice.IsDaemonLive = true;
            voice.Result = new VoiceCommandResult(VoiceCommandOutcome.Delivered, "opened https://youtube.com", Ok: true);
        };
        context.ViewModel.MarkRecordingAsCommand();
        await context.Record();

        Assert.NotEmpty(context.DaemonStarts);
        Assert.Equal(2, context.Voice.Sent.Count);   // exactly once more, never twice more
        Assert.Equal(CommandStage.Succeeded, context.Pill.Last().Stage);
        Assert.Equal("opened https://youtube.com", context.Pill.Last().Detail);
        Assert.Empty(context.Clipboard.Writes);
    });

    [Fact]
    public Task WithNoStartUpEntryItSaysSoInsteadOfWaiting() => ui.Run(async () =>
    {
        using var context = new Context();
        context.ShimPresent = false;
        context.Voice.IsDaemonLive = false;
        context.Voice.Result = new VoiceCommandResult(VoiceCommandOutcome.NotReached, "No command daemon is listening");
        context.ViewModel.MarkRecordingAsCommand();
        await context.Record();

        Assert.Empty(context.DaemonStarts);
        Assert.Single(context.Voice.Sent);
        Assert.Equal(CommandStage.Failed, context.Pill.Last().Stage);
        Assert.Empty(context.Clipboard.Writes);
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
        Assert.Equal(CommandStage.Failed, context.Pill.Last().Stage);
        Assert.Contains("No answer", context.Pill.Last().Detail);
    });

    [Fact]
    public Task AnUnconfiguredCommandModeSaysSoAndStillTypesNothing() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Voice.IsConfigured = false;
        context.Voice.IsDaemonLive = false;
        context.ViewModel.MarkRecordingAsCommand();
        await context.Record();

        Assert.Empty(context.Voice.Sent);
        Assert.Empty(context.Clipboard.Writes);
        Assert.Equal(0, context.Paste.Pasted);
        Assert.Equal(CommandStage.Failed, context.Pill.Last().Stage);
    });

    [Fact]
    public Task AGoalThatStopsToAskReadsAsAQuestionAndNotAFailure() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Voice.Result = new VoiceCommandResult(VoiceCommandOutcome.Delivered, "Working on the goal.", Ok: true, GoalId: "g-9");
        context.Voice.Updates.Add(new VoiceGoalUpdate("needs-confirm",
            "Click \"Vocal Chillstep Mix\" in Spotify? Say yes, or cancel."));
        context.ViewModel.MarkRecordingAsCommand();
        await context.Record();

        for (var i = 0; i < 60 && context.Pill.Last().Stage != CommandStage.Asking; i++) await Task.Delay(25);
        var final = context.Pill.Last();
        Assert.Equal(CommandStage.Asking, final.Stage);
        Assert.Contains("Say yes", final.Detail);
        // Amber, not red, and no alarming toast: it is his turn, not a fault.
        Assert.DoesNotContain(context.Warnings, w => w.Contains("Vocal Chillstep"));
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
    public Task OrdinaryDictationIsNeverPreviewedWhileSpeaking() => ui.Run(async () =>
    {
        // Alt+Q must cost exactly what it always did: one pass, at the end.
        using var context = new Context();
        await context.ViewModel.StartListeningAsync();
        Assert.False(context.ViewModel.CanPreviewLive);
        await context.ViewModel.StopListeningAndTranscribeAsync();
    });

    [Fact]
    public Task ACommandRecordingMayBePreviewedWhileSpeaking() => ui.Run(async () =>
    {
        using var context = new Context();
        context.ViewModel.MarkRecordingAsCommand();
        await context.ViewModel.StartListeningAsync();
        Assert.True(context.ViewModel.CanPreviewLive);
        await context.ViewModel.StopListeningAndTranscribeAsync();
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
