using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Talkty.App;
using Talkty.App.Services;
using Xunit;

namespace Talkty.Tests;

/// <summary>
/// The transport's failure paths, against a real local socket.
///
/// The paid qualification run only ever exercised the happy path — 24 successful evaluations prove
/// nothing about what happens when the provider returns a 500, hangs, or sends back a megabyte.
/// Those are the branches that decide whether a dictation survives a bad afternoon, so they get a
/// real server rather than a mock.
/// </summary>
public class JevTransportTests
{
    /// <summary>
    /// A throwaway HTTP server. Returns its URL and captures the single request it receives, so a
    /// test can assert what actually went out on the wire rather than what BuildRequest returned.
    /// </summary>
    private sealed class Server : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Thread _thread;

        public string Url { get; }
        public string? RequestBody { get; private set; }
        public string? AuthorizationHeader { get; private set; }

        public Server(int status, string body, int delayMs = 0, bool dropConnection = false)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/api/alpha/decisions";

            _thread = new Thread(() =>
            {
                try
                {
                    using var client = _listener.AcceptTcpClient();
                    using var stream = client.GetStream();

                    // Read headers, then exactly Content-Length bytes of body.
                    var head = new StringBuilder();
                    var one = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (stream.Read(one, 0, 1) <= 0) return;
                        head.Append((char)one[0]);
                    }
                    var headers = head.ToString();
                    AuthorizationHeader = headers
                        .Split("\r\n")
                        .FirstOrDefault(h => h.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase));

                    var lengthLine = headers.Split("\r\n")
                        .FirstOrDefault(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    if (lengthLine != null && int.TryParse(lengthLine.Split(':')[1].Trim(), out var length))
                    {
                        var payload = new byte[length];
                        var read = 0;
                        while (read < length)
                        {
                            var n = stream.Read(payload, read, length - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        RequestBody = Encoding.UTF8.GetString(payload, 0, read);
                    }

                    if (dropConnection) return;
                    if (delayMs > 0) Thread.Sleep(delayMs);

                    var bytes = Encoding.UTF8.GetBytes(body);
                    var response = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\n" +
                        $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    stream.Write(response);
                    stream.Write(bytes);
                    stream.Flush();
                }
                catch (IOException) { /* the client gave up first — that is the point of some tests */ }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }) { IsBackground = true };
            _thread.Start();
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { /* best effort */ }
        }
    }

    private static IReadOnlyList<JevQuestion> Questions() => new[]
    {
        new JevQuestion("c1", JevQuestionKind.Choice, "Evaluate clause c1", new Dictionary<string, string>
        {
            ["preserved"] = "kept",
            ["omitted"] = "gone"
        })
    };

    private static JsonNode State() => new JsonObject { ["prompt"] = "a generated prompt" };

    private const string ValidBody =
        """
        {"model":"typesafe/jev-1.13-20260917",
         "answers":{"c1":{"type":"choice","choice":"omitted","confidence":0.9,
                          "probabilities":{"omitted":0.95,"preserved":0.05}}},
         "usage":{"input_tokens":1200,"output_tokens":8,"cost":0.0000504}}
        """;

    private static Task<JevResult> Evaluate(Server server, CancellationToken ct = default) =>
        new JevDecisionClient(server.Url).EvaluateAsync("test-key", State(), Questions(), ct);

    [Fact]
    public async Task ASuccessfulResponseIsValidatedAndTimed()
    {
        using var server = new Server(200, ValidBody);
        var result = await Evaluate(server);

        Assert.Equal(JevStatus.Evaluated, result.Status);
        Assert.Equal("omitted", result.Evaluation!.Answers["c1"].Choice);
        Assert.Equal(1200, result.Evaluation.InputTokens);
    }

    [Fact]
    public async Task TheWireRequestCarriesTheBearerTokenAndThePinnedPrivacyPosture()
    {
        using var server = new Server(200, ValidBody);
        await Evaluate(server);

        Assert.NotNull(server.RequestBody);
        using var sent = JsonDocument.Parse(server.RequestBody!);
        Assert.Equal("typesafe/jev-1.13", sent.RootElement.GetProperty("model").GetString());

        var provider = sent.RootElement.GetProperty("provider");
        Assert.Equal("deny", provider.GetProperty("data_collection").GetString());
        Assert.True(provider.GetProperty("zdr").GetBoolean());
        Assert.False(provider.GetProperty("allow_fallbacks").GetBoolean());

        Assert.Equal("Authorization: Bearer test-key", server.AuthorizationHeader);
        // The key belongs in the header, never in the body we are about to log or persist.
        Assert.DoesNotContain("test-key", server.RequestBody!);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task ANonSuccessStatusIsUnavailableAndNeverLeaksTheProviderBody(int status)
    {
        using var server = new Server(status, """{"error":{"message":"sensitive provider detail"}}""");
        var result = await Evaluate(server);

        Assert.Equal(JevStatus.Unavailable, result.Status);
        Assert.Equal($"http_{status}", result.FailureCode);
        Assert.Null(result.Evaluation);
        // Failure codes are persisted and logged, so they must stay machine-shaped.
        Assert.DoesNotContain("sensitive", result.FailureCode!);
    }

    [Fact]
    public async Task AnOversizedResponseIsRefusedRatherThanBuffered()
    {
        using var server = new Server(200, new string('x', Constants.JevMaxResponseBytes + 1));
        var result = await Evaluate(server);

        Assert.Equal(JevStatus.Unavailable, result.Status);
        Assert.Equal("response_too_large", result.FailureCode);
    }

    [Fact]
    public async Task AProviderThatHangsPastTheDeadlineTimesOut()
    {
        using var server = new Server(200, ValidBody, delayMs: Constants.JevDecisionTimeoutMs + 3_000);
        var started = System.Diagnostics.Stopwatch.StartNew();

        var result = await Evaluate(server);
        started.Stop();

        Assert.Equal(JevStatus.Unavailable, result.Status);
        Assert.Equal("timeout", result.FailureCode);
        // The deadline is the point: the check must not outlive the dictation it belongs to.
        Assert.True(started.ElapsedMilliseconds < Constants.JevDecisionTimeoutMs + 2_000,
            $"gave up after {started.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ADroppedConnectionIsUnavailableNotACrash()
    {
        using var server = new Server(200, ValidBody, dropConnection: true);
        var result = await Evaluate(server);

        Assert.Equal(JevStatus.Unavailable, result.Status);
        Assert.Null(result.Evaluation);
    }

    [Fact]
    public async Task AGarbledBodyIsInvalidNotEvaluated()
    {
        using var server = new Server(200, "{not json");
        var result = await Evaluate(server);

        Assert.Equal(JevStatus.Invalid, result.Status);
        Assert.Equal("invalid_json", result.FailureCode);
    }

    [Fact]
    public async Task AnAnswerOutsideTheSuppliedOptionsIsRejectedOverTheWire()
    {
        using var server = new Server(200,
            """{"model":"typesafe/jev-1.13","answers":{"c1":{"type":"choice","choice":"invented","confidence":0.9,"probabilities":{"omitted":0.95,"preserved":0.05}}},"usage":{"input_tokens":1,"output_tokens":1}}""");
        var result = await Evaluate(server);

        Assert.Equal(JevStatus.Invalid, result.Status);
        Assert.Equal("unknown_choice", result.FailureCode);
    }

    [Fact]
    public async Task CallerCancellationIsDistinctFromATimeout()
    {
        using var server = new Server(200, ValidBody, delayMs: 3_000);
        using var cts = new CancellationTokenSource(200);

        var result = await Evaluate(server, cts.Token);

        // ESC and a slow provider are different events and the record must be able to tell them apart.
        Assert.Equal(JevStatus.Cancelled, result.Status);
        Assert.Equal("cancelled", result.FailureCode);
    }

    [Fact]
    public async Task AnUnreachableHostIsUnavailable()
    {
        // Port 1 on loopback: nothing listens, so the connection is refused immediately.
        var client = new JevDecisionClient("http://127.0.0.1:1/api/alpha/decisions");
        var result = await client.EvaluateAsync("test-key", State(), Questions());

        Assert.Equal(JevStatus.Unavailable, result.Status);
        Assert.Equal("network_unavailable", result.FailureCode);
    }
}
