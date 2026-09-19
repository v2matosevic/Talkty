using Talkty.App;
using Talkty.App.Models;
using Talkty.App.Services;
using Talkty.App.ViewModels;
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

    /// <summary>
    /// While Talkty sits in the tray — the normal case, because you are dictating into another app
    /// — MainWindow routes a Warning toast to a Windows tray balloon, and the shell truncates
    /// balloon text at 256 characters without saying so. The worst realistic two-concern message
    /// measured 326 characters before this was bounded.
    /// </summary>
    [Fact]
    public void AConcernMessageAlwaysFitsInATrayBalloon()
    {
        var longestLabel = PromptFidelityAnalyzer.LabelDroppedProhibition;
        var longSentence = new string('x', 400);

        foreach (var count in new[] { 1, Constants.JevMaxSurfacedConcerns })
        {
            var concerns = Enumerable.Range(0, count)
                .Select(_ => new FidelityConcern(
                    FidelityConcernKind.DroppedProhibition, longestLabel, longSentence, "c1", 0.99, 0.99))
                .ToList();

            var message = MainViewModel.BuildConcernMessage(concerns);

            Assert.True(message.Length <= Constants.FidelityToastMaxChars,
                $"{count} concern(s) produced {message.Length} chars, over the {Constants.FidelityToastMaxChars} ceiling");
            // 256 is the shell's hard limit; the ceiling exists to stay clear of it.
            Assert.True(message.Length < 256);
            Assert.StartsWith("Prompt check", message);
            Assert.Contains(longestLabel, message);
        }
    }

    [Fact]
    public void ALongQuoteIsCutAtAWordBoundaryNotMidWord()
    {
        var concern = new FidelityConcern(
            FidelityConcernKind.OmittedClause, PromptFidelityAnalyzer.LabelOmittedClause,
            "Oh and make sure the whole page still works on mobile, it is quite cramped at the moment and I keep forgetting to check it.",
            "c5", 0.97, 0.93);

        var message = MainViewModel.BuildConcernMessage(new[] { concern });

        var quote = message[(message.IndexOf('“') + 1)..message.LastIndexOf('”')];
        Assert.EndsWith("…", quote);
        // Cutting mid-word ("at the moment a...") reads like a bug in the quote, not a shortening.
        var lastWord = quote.TrimEnd('…').Split(' ')[^1];
        Assert.Contains(lastWord, concern.SourceText.Split(' '));
    }

    [Fact]
    public void AQuoteThatEndsExactlyOnAWordKeepsThatWord()
    {
        // The budget landing on a word boundary must not cost the user the last whole word.
        var text = new string('a', Constants.FidelityQuoteCharsSingle) + " tail";
        var concern = new FidelityConcern(
            FidelityConcernKind.OmittedClause, PromptFidelityAnalyzer.LabelOmittedClause, text, "c1", 0.9, 0.9);

        var message = MainViewModel.BuildConcernMessage(new[] { concern });

        Assert.Contains(new string('a', Constants.FidelityQuoteCharsSingle), message);
    }

    [Fact]
    public void AShortConcernIsQuotedInFullNotPaddedOrCut()
    {
        var concern = new FidelityConcern(
            FidelityConcernKind.OmittedClause, PromptFidelityAnalyzer.LabelOmittedClause,
            "Do not deploy this to production.", "c2", 0.97, 0.93);

        var message = MainViewModel.BuildConcernMessage(new[] { concern });

        Assert.Contains("Do not deploy this to production.", message);
        Assert.DoesNotContain("…", message);
    }

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
