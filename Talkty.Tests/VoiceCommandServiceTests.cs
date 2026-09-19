using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Talkty.App;
using Talkty.App.Models;
using Talkty.App.Services;
using Xunit;

namespace Talkty.Tests;

/// <summary>
/// The transport for command mode. Three things matter here: it only ever talks to
/// this machine, it never throws into the recording pipeline, and it tells the caller
/// the difference between "it did not run" and "it might have".
/// </summary>
public class VoiceCommandServiceTests
{
    [Fact]
    public async Task CapturedWindowMetadataTravelsWithTheOriginalCommand()
    {
        string? body = null;
        var handler = new StubHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, """{"outcome":"working","detail":"Working"}""");
        });
        var target = new CapturedWindowInfo("1234", 55, "brave", "Original page", "2026-09-19T10:00:00.0000000Z");
        await Build(handler).DispatchAsync("search this", target.ProcessName, default, target);
        using var json = JsonDocument.Parse(body!);
        var snapshot = json.RootElement.GetProperty("targetWindow");
        Assert.Equal("1234", snapshot.GetProperty("handle").GetString());
        Assert.Equal(55, snapshot.GetProperty("pid").GetInt32());
        Assert.Equal("Original page", snapshot.GetProperty("title").GetString());
        Assert.Equal(target.ProcessStartedAt, snapshot.GetProperty("startedAt").GetString());
        Assert.Equal("search this", json.RootElement.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("http://127.0.0.1:8765/command", true)]
    [InlineData("http://localhost:8765/command", true)]
    [InlineData("http://[::1]:8765/command", true)]
    [InlineData("https://127.0.0.1:8765/command", true)]
    [InlineData("http://192.168.1.50:8765/command", false)]
    [InlineData("https://example.com/command", false)]
    [InlineData("ftp://127.0.0.1/command", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyThisMachineCountsAsAnEndpoint(string? endpoint, bool expected)
        => Assert.Equal(expected, VoiceCommandService.IsLoopback(endpoint));

    [Fact]
    public async Task ANonLocalEndpointIsRefusedWithoutSendingAnything()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not send"));
        var service = Build(handler, s => s.CommandEndpoint = "https://example.com/command");

        var result = await service.DispatchAsync("open chrome", null, default);

        Assert.Equal(VoiceCommandOutcome.NotReached, result.Outcome);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ASuccessfulCallIsDeliveredAndCarriesTheDaemonsDetail()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{"ok":true,"outcome":"executed","detail":"opened https://youtube.com","commandId":"os.open_site"}"""));
        var service = Build(handler);

        var result = await service.DispatchAsync("open youtube", null, default);

        Assert.Equal(VoiceCommandOutcome.Delivered, result.Outcome);
        Assert.Equal("opened https://youtube.com", result.Message);
        Assert.Equal("os.open_site", result.CommandId);
    }

    [Fact]
    public async Task TheTokenTravelsInItsHeaderAndTheTextInTheBody()
    {
        HttpRequestMessage? seen = null;
        string? body = null;
        var handler = new StubHandler(r =>
        {
            seen = r;
            body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, """{"ok":true,"outcome":"recorded"}""");
        });
        var service = Build(handler, s => s.CommandToken = "abc123");

        await service.DispatchAsync("open chrome", "Code.exe", default);

        Assert.Equal("abc123", seen!.Headers.GetValues("x-voice-token").Single());
        Assert.Contains("\"text\":\"open chrome\"", body);
        Assert.Contains("\"hotkey\":\"command\"", body);
        Assert.Contains("Code.exe", body);
    }

    [Fact]
    public async Task ARejectedTokenIsNotReachedSoDictationCanTakeOver()
    {
        var service = Build(new StubHandler(_ => Json(HttpStatusCode.Unauthorized, "{}")));
        var result = await service.DispatchAsync("open chrome", null, default);

        Assert.Equal(VoiceCommandOutcome.NotReached, result.Outcome);
        Assert.Contains("token", result.Message);
    }

    [Fact]
    public async Task AServerErrorIsNotReached()
    {
        var service = Build(new StubHandler(_ => Json(HttpStatusCode.InternalServerError, "{}")));
        var result = await service.DispatchAsync("open chrome", null, default);

        Assert.Equal(VoiceCommandOutcome.NotReached, result.Outcome);
        Assert.Contains("500", result.Message);
    }

    [Fact]
    public async Task NothingListeningIsNotReached()
    {
        var service = Build(new StubHandler(_ => throw new HttpRequestException("refused")));
        var result = await service.DispatchAsync("open chrome", null, default);

        Assert.Equal(VoiceCommandOutcome.NotReached, result.Outcome);
    }

    [Fact]
    public async Task ATimeoutIsUncertainBecauseTheCommandMayHaveRun()
    {
        var service = Build(new StubHandler(_ => throw new TaskCanceledException("timed out")));
        var result = await service.DispatchAsync("delete the branch", null, default);

        Assert.Equal(VoiceCommandOutcome.Uncertain, result.Outcome);
        Assert.False(result.Delivered);
    }

    [Fact]
    public async Task AnUnexpectedFailureNeverEscapesIntoTheRecordingPipeline()
    {
        var service = Build(new StubHandler(_ => throw new InvalidOperationException("boom")));
        var result = await service.DispatchAsync("open chrome", null, default);

        Assert.Equal(VoiceCommandOutcome.NotReached, result.Outcome);
    }

    [Fact]
    public void AnAnswerWithoutDetailFallsBackToItsOutcome()
        => Assert.Equal("no-match", VoiceCommandService.Read("""{"ok":false,"outcome":"no-match"}""").Message);

    [Fact]
    public void AnUnreadableAnswerStillCountsAsDelivered()
    {
        // It answered. We cannot parse it, but the command was received, so falling
        // back to dictation would risk acting twice.
        var result = VoiceCommandService.Read("<html>not json</html>");
        Assert.Equal(VoiceCommandOutcome.Delivered, result.Outcome);
    }

    [Fact]
    public void ALongAnswerIsBoundedToToastLength()
    {
        var long_ = new string('x', 500);
        var result = VoiceCommandService.Read($$"""{"detail":"{{long_}}"}""");
        Assert.True(result.Message.Length <= Constants.VoiceCommandMessageMaxChars + 1);
        Assert.EndsWith("…", result.Message);
    }

    [Fact]
    public async Task ALiveServiceRecordDecidesWhereTheCommandGoes()
    {
        // The daemon moved to another port. Nothing in settings knows that, and
        // nothing has to: the record it wrote when it started does.
        HttpRequestMessage? seen = null;
        var handler = new StubHandler(r => { seen = r; return Json(HttpStatusCode.OK, """{"ok":true,"outcome":"executed","detail":"done"}"""); });
        var service = Build(handler,
            s => { s.CommandEndpoint = "http://127.0.0.1:8765/command"; s.CommandToken = "old-token"; },
            () => new VoiceEndpointResolver.Endpoint("http://127.0.0.1:17765/command", "record-token", 4242));

        var result = await service.DispatchAsync("mute", null, default);

        Assert.Equal(VoiceCommandOutcome.Delivered, result.Outcome);
        Assert.Equal("http://127.0.0.1:17765/command", seen!.RequestUri!.ToString());
        Assert.Equal("record-token", seen.Headers.GetValues("x-voice-token").Single());
        Assert.Equal(0, handler.HealthChecks);
    }

    [Fact]
    public async Task WithoutARecordNothingIsSentUntilThePortIdentifiesItselfAsTheDaemon()
    {
        // This is the failure that happened for real: the daemon was down and
        // another program held its port, so spoken commands reached a stranger.
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"ok":true}"""))
        {
            HealthBody = """{"error":"Not found"}""",
        };
        var service = Build(handler);

        var result = await service.DispatchAsync("open youtube", null, default);

        Assert.Equal(VoiceCommandOutcome.NotReached, result.Outcome);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(1, handler.HealthChecks);
    }

    [Fact]
    public async Task WithoutARecordTheConfiguredEndpointStillWorksWhenItIsTheDaemon()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"ok":true,"outcome":"executed","detail":"muted"}"""));
        var service = Build(handler);

        var result = await service.DispatchAsync("mute", null, default);

        Assert.Equal(VoiceCommandOutcome.Delivered, result.Outcome);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("""{"service":"hermes-voice","pid":4242,"url":"http://127.0.0.1:17765","command":"/command","token":"t"}""", "http://127.0.0.1:17765/command")]
    [InlineData("""{"service":"hermes-voice","pid":4242,"url":"http://127.0.0.1:17765"}""", "http://127.0.0.1:17765/command")]
    public void AGoodRecordResolvesToTheDaemonsCommandUrl(string json, string expected)
    {
        var endpoint = VoiceEndpointResolver.Parse(json, _ => true);
        Assert.Equal(expected, endpoint!.Url);
    }

    [Theory]
    [InlineData("""{"service":"something-else","pid":4242,"url":"http://127.0.0.1:17765"}""", true)]
    [InlineData("""{"service":"hermes-voice","pid":4242,"url":"http://10.0.0.4:17765"}""", true)]
    [InlineData("""{"service":"hermes-voice","url":"http://127.0.0.1:17765"}""", true)]
    [InlineData("""{"service":"hermes-voice","pid":4242,"url":"http://127.0.0.1:17765"}""", false)]
    public void ARecordThatIsNotOursOrNotAliveIsIgnored(string json, bool alive)
        => Assert.Null(VoiceEndpointResolver.Parse(json, _ => alive));

    [Fact]
    public void ARecordMakesCommandModeConfiguredEvenWithEmptySettings()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var service = Build(handler, s => { s.CommandEndpoint = ""; s.CommandToken = ""; },
            () => new VoiceEndpointResolver.Endpoint("http://127.0.0.1:17765/command", "t", 1));
        Assert.True(service.IsConfigured);
    }

    [Fact]
    public void ConfigurationNeedsAnEndpointATokenAndALocalAddress()
    {
        Assert.True(Build(new StubHandler(_ => Json(HttpStatusCode.OK, "{}"))).IsConfigured);
        Assert.False(Build(new StubHandler(_ => Json(HttpStatusCode.OK, "{}")), s => s.CommandToken = "").IsConfigured);
        Assert.False(Build(new StubHandler(_ => Json(HttpStatusCode.OK, "{}")),
            s => s.CommandEndpoint = "https://example.com/x").IsConfigured);
    }

    private static VoiceCommandService Build(
        StubHandler handler,
        Action<AppSettings>? configure = null,
        Func<VoiceEndpointResolver.Endpoint?>? resolve = null)
    {
        // Reuses the flow tests' settings fake so there is one ISettingsService stub.
        var service = new TranscriptionFlowTests.FakeSettings();
        service.Settings.CommandMode = true;
        service.Settings.CommandToken = "token";
        configure?.Invoke(service.Settings);
        // No service record unless a test supplies one, so these stay hermetic
        // whatever is running on the machine.
        return new VoiceCommandService(service, new HttpClient(handler), resolve ?? (() => null));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public int HealthChecks { get; private set; }

        /// <summary>Answer the identity probe as the daemon unless a test says otherwise.</summary>
        public string HealthBody { get; set; } = """{"ok":true,"service":"hermes-voice"}""";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/health")
            {
                HealthChecks++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(HealthBody, Encoding.UTF8, "application/json"),
                });
            }
            Calls++;
            return Task.FromResult(respond(request));
        }
    }
}
