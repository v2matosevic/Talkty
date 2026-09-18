using Talkty.App.Models;
using Talkty.App.Services;
using Xunit;

namespace Talkty.Tests;

/// <summary>
/// The fidelity check inside the real view-model pipeline. The contract it has to keep is mostly
/// about what it must NOT disturb: the delivered text, the clipboard, the paste, and the user's
/// dictation when anything goes wrong.
/// </summary>
public partial class TranscriptionFlowTests
{
    [Fact]
    public Task RefinedPromptIsCheckedAgainstTheDictationItCameFrom() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Engine.Text = "Rename getUserById and do not deploy it.";
        context.Refiner.Run = _ => Task.FromResult<string?>("**Task**\nRename getUserByID.");

        await context.ViewModel.StartListeningAsync();
        context.ViewModel.PromptMode = true;
        await context.ViewModel.StopListeningAndTranscribeAsync();
        await context.Fidelity.Called;

        Assert.Equal(1, context.Fidelity.Calls);
        // It is handed the RAW transcription and the generated prompt — not the post-processed
        // history text, and never the audio.
        Assert.Contains("getUserById", context.Fidelity.LastTranscript!);
        Assert.Equal("**Task**\nRename getUserByID.", context.Fidelity.LastRewrite);

        // Delivery is untouched: the prompt still reached the clipboard and was pasted.
        Assert.Equal("**Task**\nRename getUserByID.", context.Clipboard.Text);
        Assert.Equal(1, context.Paste.Pasted);
        Assert.Single(context.ViewModel.History);
    });

    [Fact]
    public Task PlainDictationIsNeverChecked() => ui.Run(async () =>
    {
        using var context = new Context();
        await context.Record();

        // Nothing was rewritten, so there is nothing to compare and no reason to spend anything.
        Assert.Equal(0, context.Fidelity.Calls);
        Assert.Equal(1, context.Paste.Pasted);
    });

    [Fact]
    public Task FailedRefinementIsNeverChecked() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Refiner.Run = _ => Task.FromResult<string?>(null);

        await context.ViewModel.StartListeningAsync();
        context.ViewModel.PromptMode = true;
        await context.ViewModel.StopListeningAndTranscribeAsync();

        Assert.Equal(0, context.Fidelity.Calls);
        // The raw transcription still reached the user, with the existing warning.
        Assert.Single(context.ViewModel.History);
        Assert.Contains(context.Warnings, m => m.Contains("Prompting failed"));
    });

    [Fact]
    public Task OffModeSendsNothing() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Fidelity.Mode = PromptFidelityMode.Off;
        context.Refiner.Run = _ => Task.FromResult<string?>("A generated prompt");

        await context.ViewModel.StartListeningAsync();
        context.ViewModel.PromptMode = true;
        await context.ViewModel.StopListeningAndTranscribeAsync();

        Assert.Equal(0, context.Fidelity.Calls);
        Assert.Equal("A generated prompt", context.Clipboard.Text);
    });

    [Fact]
    public Task ARaisedConcernQuotesTheUsersOwnWordsWithAFixedLabel() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Fidelity.Mode = PromptFidelityMode.Review;
        context.Fidelity.RaiseOnEvaluate.Add(new FidelityConcern(
            FidelityConcernKind.OmittedClause,
            PromptFidelityAnalyzer.LabelOmittedClause,
            "Do not deploy this to production.",
            "c2", 0.97, 0.93));
        context.Refiner.Run = _ => Task.FromResult<string?>("A generated prompt");

        await context.ViewModel.StartListeningAsync();
        context.ViewModel.PromptMode = true;
        await context.ViewModel.StopListeningAndTranscribeAsync();
        await context.Fidelity.Called;
        await context.ViewModel.PendingHistorySave;
        await Task.Yield();

        var concernToast = context.Warnings.FirstOrDefault(m => m.Contains("Prompt check"));
        Assert.NotNull(concernToast);
        Assert.Contains(PromptFidelityAnalyzer.LabelOmittedClause, concernToast);
        Assert.Contains("Do not deploy this to production.", concernToast);

        // The concern is advisory only: the prompt was delivered exactly as generated.
        Assert.Equal("A generated prompt", context.Clipboard.Text);
        Assert.Equal(1, context.Paste.Pasted);
    });

    [Fact]
    public Task SavingSettingsPersistsTheModeAndForwardsItLive() => ui.Run(() =>
    {
        using var context = new Context();
        Assert.Equal(PromptFidelityMode.RecordOnly, new AppSettings().PromptFidelity);

        var saved = new AppSettings { PromptFidelity = PromptFidelityMode.Review };
        context.ViewModel.ApplySettings(saved);

        // Both halves of the v1.1.3 lesson: written onto the persisted settings AND pushed to the
        // live service. Missing either one makes the setting look like it works until a restart.
        Assert.Equal(PromptFidelityMode.Review, context.Settings.Settings.PromptFidelity);
        Assert.Equal(PromptFidelityMode.Review, context.Fidelity.Mode);
        return Task.CompletedTask;
    });

    [Fact]
    public Task EscapeAndANewRecordingBothStopAPendingCheck() => ui.Run(async () =>
    {
        using var context = new Context();

        await context.ViewModel.StartListeningAsync();
        var afterStart = context.Fidelity.Cancellations;
        Assert.True(afterStart >= 1, "starting a recording must supersede a check from the previous one");

        context.ViewModel.CancelRecording();
        Assert.True(context.Fidelity.Cancellations > afterStart, "ESC must cancel a pending check");
    });
}
