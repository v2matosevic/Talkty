using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Version2.Capture;
using Xunit;

namespace Talkty.Tests;

public partial class TranscriptionFlowTests
{
    private static readonly CaptureDestination HubTarget = new("ws-one", "Workspace one", "tile-one", "Agent one", "session-one", "main");
    private static readonly JsonSerializerOptions HubJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string HubTemp() => Path.Combine(Path.GetTempPath(), "Talkty-capture-tests", Guid.NewGuid().ToString("N"));
    private static HttpResponseMessage HubReply(HttpStatusCode status, object body) => new(status) { Content = new StringContent(JsonSerializer.Serialize(body, HubJson), Encoding.UTF8, "application/json") };
    private sealed class HubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request);
    }
    private static CaptureClient HubClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, string? folder = null, string instance = "fixture-instance") => new(folder ?? HubTemp(), new HttpClient(new HubHandler(request =>
    {
        if (request.RequestUri!.AbsolutePath == "/health") return Task.FromResult(HubReply(HttpStatusCode.OK, new { service = CaptureClient.Service, instanceId = instance, protocolVersion = 1 }));
        return respond(request);
    })), () => new CaptureEndpoint("http://127.0.0.1:17777", "fixture-token", instance));

    [Fact]
    public Task HubDictationUsesTheFrozenTargetAndNeverTouchesClipboardOrPaste() => ui.Run(async () =>
    {
        CaptureSubmission? sent = null;
        var client = HubClient(async request =>
        {
            sent = JsonSerializer.Deserialize<CaptureSubmission>(await request.Content!.ReadAsStringAsync(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return HubReply(HttpStatusCode.OK, new CaptureReceipt(sent!.OperationId, sent.Destination, "staged", 1, "Saved to the selected draft."));
        });
        using var context = new Context(captureClient: client);
        context.Engine.Text = "Keep 42 and the final sentence.";
        context.ViewModel.SelectCaptureDestination(HubTarget);
        await context.ViewModel.StartListeningAsync();
        context.ViewModel.SelectCaptureDestination(HubTarget with { TargetId = "other-tile" });
        await context.ViewModel.StopListeningAndTranscribeAsync();
        Assert.Equal(HubTarget, sent!.Destination);
        Assert.Equal("Keep 42 and the final sentence.", sent.Text);
        Assert.Empty(context.Clipboard.Writes); Assert.Equal(0, context.Paste.Pasted);
        Assert.Single(context.ViewModel.History); Assert.Empty(client.Pending());
    });

    [Fact]
    public Task UnverifiedHubDeliveryRetainsEncryptedTextAndHistoryWithoutPastingElsewhere() => ui.Run(async () =>
    {
        var directory = HubTemp(); int posts = 0;
        var client = HubClient(request =>
        {
            if (request.Method == HttpMethod.Post) { posts++; throw new HttpRequestException("fixture lost response"); }
            return Task.FromResult(HubReply(HttpStatusCode.NotFound, new { error = "No receipt" }));
        }, directory);
        using var context = new Context(captureClient: client); context.Engine.Text = "Private fixture dictation.";
        context.ViewModel.SelectCaptureDestination(HubTarget); await context.Record();
        Assert.Equal(1, posts); Assert.Single(context.ViewModel.History);
        Assert.Empty(context.Clipboard.Writes); Assert.Equal(0, context.Paste.Pasted);
        var pending = Assert.Single(client.Pending()); Assert.Equal(HubTarget, pending.Destination);
        Assert.Equal("Private fixture dictation.", pending.Text);
        Assert.DoesNotContain(pending.Text, Encoding.UTF8.GetString(File.ReadAllBytes(Assert.Single(Directory.GetFiles(directory, "*.capture")))));
        var restarted = HubClient(_ => Task.FromResult(HubReply(HttpStatusCode.NotFound, new { })), directory);
        Assert.Equal(pending.OperationId, Assert.Single(restarted.Pending()).OperationId);
    });

    [Fact]
    public async Task LostCaptureResponseReconcilesTheSameReceiptWithoutAnotherPost()
    {
        var submission = CaptureClient.Text(HubTarget, "The complete instruction."); int posts = 0;
        var client = HubClient(request =>
        {
            if (request.Method == HttpMethod.Post) { posts++; throw new HttpRequestException("lost after staging"); }
            Assert.EndsWith(submission.OperationId, request.RequestUri!.AbsolutePath);
            return Task.FromResult(HubReply(HttpStatusCode.OK, new CaptureReceipt(submission.OperationId, HubTarget, "staged", 1, "Stored once.")));
        });
        Assert.True((await client.StageAsync(submission)).Accepted); Assert.Equal(1, posts); Assert.Empty(client.Pending());
    }

    [Fact]
    public async Task WrongReceiptAndWrongServiceNeverCountAsDelivery()
    {
        var client = HubClient(_ => Task.FromResult(HubReply(HttpStatusCode.OK, new CaptureReceipt(Guid.NewGuid().ToString("D"), HubTarget, "staged", 1, "wrong"))));
        Assert.False((await client.StageAsync(CaptureClient.Text(HubTarget, "Keep this."))).Accepted);
        Assert.Single(client.Pending());
        int posts = 0;
        var stranger = new CaptureClient(HubTemp(), new HttpClient(new HubHandler(request =>
        { if (request.Method == HttpMethod.Post) posts++; return Task.FromResult(HubReply(HttpStatusCode.OK, new { service = "other-service" })); })),
            () => new CaptureEndpoint("http://127.0.0.1:17777", "fixture-token", "fixture-instance"));
        Assert.False((await stranger.StageAsync(CaptureClient.Text(HubTarget, "Keep this."))).Accepted);
        Assert.Equal(0, posts); Assert.Single(stranger.Pending());
    }

    [Fact]
    public async Task ARepeatedOperationCannotChangeItsFrozenPayload()
    {
        var client = HubClient(_ => Task.FromResult(HubReply(HttpStatusCode.Conflict, new { error = "Target closed" })));
        var submission = CaptureClient.Text(HubTarget, "One take."); await client.StageAsync(submission);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StageAsync(submission with { Text = "Changed take." }));
        Assert.Equal("One take.", Assert.Single(client.Pending()).Text);
    }

    [Fact]
    public void TheWireContractDoesNotSerializeDisplayOnlyFields()
    {
        var body = JsonSerializer.Serialize(CaptureClient.Text(HubTarget, "Fixture"), HubJson);
        Assert.DoesNotContain("label", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public Task CaptureDestinationAndSavedDeliveryControlsFitTheSmallWindow() => ui.Run(() =>
    {
        var surface = LoadPreview("Talkty.App/Views/CaptureHubWindow.xaml");
        var destinations = (System.Windows.Controls.ComboBox)surface.FindName("Destinations");
        destinations.ItemsSource = new[] { HubTarget }; destinations.SelectedIndex = 0;
        var saved = (System.Windows.Controls.ListBox)surface.FindName("Saved");
        saved.ItemsSource = Enumerable.Range(0, 12).Select(_ => CaptureClient.Text(HubTarget, "A saved instruction that keeps the last sentence.")).ToArray();
        var status = (System.Windows.Controls.TextBlock)surface.FindName("Status");
        status.Text = "Saved for retry. The selected draft remains fixed.";
        Layout(surface, 510, 410);
        foreach (var name in new[] { "UseButton", "RetryButton", "Destinations", "Saved" })
        {
            var element = (System.Windows.FrameworkElement)surface.FindName(name);
            var point = element.TranslatePoint(new System.Windows.Point(), surface);
            Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0);
            Assert.InRange(point.Y + element.ActualHeight, 0, 410);
        }
        SavePreview(surface, 510, 410, "capture-hub.png");
        return Task.CompletedTask;
    });

}
