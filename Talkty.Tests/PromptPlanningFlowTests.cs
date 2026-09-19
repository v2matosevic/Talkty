using Talkty.App.Models;
using Talkty.App.Services;
using Xunit;

namespace Talkty.Tests;

/// <summary>
/// Planning inside the real view-model pipeline. The classifier sits in front of the refinement
/// model, so unlike the fidelity check it CAN change what the user receives. These tests are
/// mostly about the conditions under which it is allowed to.
/// </summary>
public partial class TranscriptionFlowTests
{
    [Fact]
    public Task PlanningOffSendsNothingAndRefinesExactlyAsBefore() => ui.Run(async () =>
    {
        using var context = new Context(s => s.PromptPlanning = PromptPlanning.Off);
        context.Decisions.AnswerAlreadyAPrompt();

        await context.ViewModel.StartListeningAsync();
        context.ViewModel.PromptMode = true;
        await context.ViewModel.StopListeningAndTranscribeAsync();

        Assert.Equal(0, context.Decisions.Calls);
        Assert.Equal(1, context.Refiner.Calls);
        // No classification happened, so nothing steers the refinement.
        Assert.True(string.IsNullOrEmpty(context.Refiner.LastHint));
        Assert.Equal("A generated prompt", context.Clipboard.Text);
    });

    [Fact]
    public Task FullModeSkipsTheModelCallWhenTheDictationIsAlreadyAPrompt() => ui.Run(async () =>
    {
        using var context = new Context(s => s.PromptPlanning = PromptPlanning.Full);
        context.Engine.Text = "Add a loading spinner to the export button.";
        context.Decisions.AnswerAlreadyAPrompt();

        await context.ViewModel.StartListeningAsync();
        context.ViewModel.PromptMode = true;
        await context.ViewModel.StopListeningAndTranscribeAsync();

        // The whole point: the common one-line ask costs no generation call and no wait.
        Assert.Equal(1, context.Decisions.Calls);
        Assert.Equal(0, context.Refiner.Calls);

        // The user still gets their dictation, cleaned and delivered exactly as a plain one is.
        Assert.Equal("Add a loading spinner to the export button.", context.Clipboard.Text);
        Assert.Equal(1, context.Paste.Pasted);
        Assert.Single(context.ViewModel.History);
        Assert.Empty(context.Warnings);
    });

    [Fact]
    public Task HintsModeNeverSkipsAPromptItOnlySteersOne() => ui.Run(async () =>
    {
        using var context = new Context(s => s.PromptPlanning = PromptPlanning.Hints);
        context.Decisions.AnswerAlreadyAPrompt();

        await context.ViewModel.StartListeningAsync();
        context.ViewModel.PromptMode = true;
        await context.ViewModel.StopListeningAndTranscribeAsync();

        // Same confident "already a prompt" answer as the Full test, but Hints may not act on it.
        Assert.Equal(1, context.Decisions.Calls);
        Assert.Equal(1, context.Refiner.Calls);
        Assert.Equal("A generated prompt", context.Clipboard.Text);
    });

    [Fact]
    public Task SubstantialWorkGetsTheHintAndTheBetterModel() => ui.Run(async () =>
    {
        using var context = new Context(s => s.PromptPlanning = PromptPlanning.Hints);
        context.Decisions.AnswerNeedsRefinement();

        await context.ViewModel.StartListeningAsync();
        context.ViewModel.PromptMode = true;
        await context.ViewModel.StopListeningAndTranscribeAsync();

        Assert.Equal(1, context.Refiner.Calls);
        Assert.Contains("feature", context.Refiner.LastHint!);
        // Today the hardest dictations start on the fast model and pay for an escalation.
        Assert.True(context.Refiner.LastPreferredQualityModel);
    });

    [Fact]
    public Task AnUnavailableClassifierRefinesAsToday() => ui.Run(async () =>
    {
        using var context = new Context(s => s.PromptPlanning = PromptPlanning.Full);
        context.Decisions.Result = JevResult.Fail(JevStatus.Unavailable, "timeout");

        await context.ViewModel.StartListeningAsync();
        context.ViewModel.PromptMode = true;
        await context.ViewModel.StopListeningAndTranscribeAsync();

        // A classifier that cannot answer must never cost the user their prompt.
        Assert.Equal(1, context.Refiner.Calls);
        Assert.True(string.IsNullOrEmpty(context.Refiner.LastHint));
        Assert.False(context.Refiner.LastPreferredQualityModel);
        Assert.Equal("A generated prompt", context.Clipboard.Text);
        Assert.Empty(context.Warnings);
    });

    [Fact]
    public Task PlainDictationIsNeverClassified() => ui.Run(async () =>
    {
        using var context = new Context(s => s.PromptPlanning = PromptPlanning.Full);
        context.Decisions.AnswerAlreadyAPrompt();

        await context.Record();   // Prompting off

        // Planning exists to decide how to build a prompt. With no prompt requested there is
        // nothing to decide and nothing to spend.
        Assert.Equal(0, context.Decisions.Calls);
        Assert.Equal(0, context.Refiner.Calls);
        Assert.Equal(1, context.Paste.Pasted);
    });

    [Fact]
    public Task ASkippedRefinementIsNotRecordedAsAPromptInHistory() => ui.Run(async () =>
    {
        using var context = new Context(s => s.PromptPlanning = PromptPlanning.Full);
        context.Engine.Text = "Rename this variable to orderTotal.";
        context.Decisions.AnswerAlreadyAPrompt();

        await context.ViewModel.StartListeningAsync();
        context.ViewModel.PromptMode = true;
        await context.ViewModel.StopListeningAndTranscribeAsync();

        // Nothing was expanded, so there is no "you said" pair to show: it is a plain entry.
        var entry = Assert.Single(context.ViewModel.History);
        Assert.False(entry.IsPrompt);
        Assert.Equal("Rename this variable to orderTotal.", entry.Text);
    });

    [Fact]
    public Task ASkippedRefinementRunsNoFidelityCheck() => ui.Run(async () =>
    {
        using var context = new Context(s => s.PromptPlanning = PromptPlanning.Full);
        context.Decisions.AnswerAlreadyAPrompt();

        await context.ViewModel.StartListeningAsync();
        context.ViewModel.PromptMode = true;
        await context.ViewModel.StopListeningAndTranscribeAsync();

        // There is no rewrite, so there is nothing to compare the dictation against.
        Assert.Equal(0, context.Fidelity.Calls);
    });

    [Fact]
    public Task PlanningModePersistsAndReachesTheLiveClassifier() => ui.Run(() =>
    {
        using var context = new Context();
        Assert.Equal(PromptPlanning.Off, new AppSettings().PromptPlanning);

        context.ViewModel.ApplySettings(new AppSettings { PromptPlanning = PromptPlanning.Full });

        // The v1.1.3 lesson, again: written to disk as well as applied live.
        Assert.Equal(PromptPlanning.Full, context.Settings.Settings.PromptPlanning);
        return Task.CompletedTask;
    });
}
