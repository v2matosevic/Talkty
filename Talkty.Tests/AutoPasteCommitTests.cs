using Talkty.App.Services;
using Xunit;

namespace Talkty.Tests;

public class AutoPasteCommitTests
{
    [Fact]
    public void FailedClipboardPreparationNeverSendsInput()
    {
        var sent = false;
        Assert.Throws<InvalidOperationException>(() => AutoPasteService.CommitPaste(
            () => sent = true, () => throw new InvalidOperationException("Clipboard locked"), default));
        Assert.False(sent);
    }

    [Fact]
    public void CancelDuringClipboardPreparationNeverSendsInput()
    {
        using var cts = new CancellationTokenSource();
        var sent = false;
        Assert.Throws<OperationCanceledException>(() => AutoPasteService.CommitPaste(
            () => sent = true, cts.Cancel, cts.Token));
        Assert.False(sent);
    }

    [Fact]
    public void AlreadyCancelledPasteDoesNotTouchClipboardOrSendInput()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var prepared = false;
        var sent = false;
        Assert.Throws<OperationCanceledException>(() => AutoPasteService.CommitPaste(
            () => sent = true, () => prepared = true, cts.Token));
        Assert.False(prepared);
        Assert.False(sent);
    }

    [Fact]
    public void InputDeliveryFailureIsNotReportedAsSuccessfulPaste()
    {
        Assert.False(AutoPasteService.CommitPaste(() => false, null, default));
    }

    [Fact]
    public void PreparesClipboardBeforeSendingInput()
    {
        var prepared = false;
        Assert.True(AutoPasteService.CommitPaste(() => prepared, () => prepared = true, default));
    }
}
