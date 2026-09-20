using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Talkty.App.Services;

/// <summary>
/// Posts a command-mode dictation to the local command daemon (Hermes) and reports
/// what came back. It carries text and nothing else: no command logic lives here,
/// and the daemon owns every decision about what a sentence means.
///
/// Loopback only by design. The endpoint is a setting, but a non-local address is
/// refused rather than dialled, because this hands spoken instructions to whatever
/// answers and a typo should not send them across a network.
/// </summary>
public class VoiceCommandService : IVoiceCommandService
{
    private readonly ISettingsService _settingsService;
    private readonly HttpClient _http;
    private readonly Func<VoiceEndpointResolver.Endpoint?> _resolve;

    public VoiceCommandService(
        ISettingsService settingsService,
        HttpClient? http = null,
        Func<VoiceEndpointResolver.Endpoint?>? resolve = null)
    {
        _settingsService = settingsService;
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromMilliseconds(Constants.VoiceCommandTimeoutMs);
        _resolve = resolve ?? (() => VoiceEndpointResolver.Read());
    }

    public bool IsConfigured
    {
        get
        {
            if (_resolve() is not null) return true;
            var s = _settingsService.Settings;
            return !string.IsNullOrWhiteSpace(s.CommandEndpoint)
                   && !string.IsNullOrWhiteSpace(s.CommandToken)
                   && IsLoopback(s.CommandEndpoint);
        }
    }

    public bool IsDaemonLive => _resolve() is not null;

    /// <summary>A command endpoint must be on this machine. Anything else is refused.</summary>
    public static bool IsLoopback(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.IsLoopback) return true;
        return IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip);
    }

    public Task<VoiceCommandResult> DispatchAsync(
        string text,
        string? foregroundApp,
        CancellationToken cancellationToken)
        => DispatchAsync(text, foregroundApp, cancellationToken, null);

    public async Task<VoiceCommandResult> DispatchAsync(
        string text,
        string? foregroundApp,
        CancellationToken cancellationToken,
        CapturedWindowInfo? targetWindow)
    {
        var settings = _settingsService.Settings;

        // The daemon's own record wins: it is written by the daemon that is
        // running now, so a moved port costs nothing and a squatted one is not
        // dialled. Without a record, the configured endpoint is used only after
        // whatever is listening identifies itself as the daemon.
        var record = _resolve();
        var endpoint = record?.Url ?? settings.CommandEndpoint;
        var token = record?.Token ?? settings.CommandToken;

        if (!IsLoopback(endpoint))
        {
            Log.Warning($"Command endpoint is not on this machine: {endpoint}");
            return new VoiceCommandResult(VoiceCommandOutcome.NotReached,
                "Command endpoint must be a local address");
        }

        if (record is null && !await AnswersAsTheDaemonAsync(endpoint, cancellationToken))
        {
            return new VoiceCommandResult(VoiceCommandOutcome.NotReached,
                "No command daemon is listening — run `hermes voice ensure`");
        }

        var payload = JsonSerializer.Serialize(new
        {
            text,
            hotkey = "command",
            foregroundApp,
            targetWindow = targetWindow == null ? null : new
            {
                handle = targetWindow.Handle,
                pid = targetWindow.ProcessId,
                processName = targetWindow.ProcessName,
                title = targetWindow.Title,
                startedAt = targetWindow.ProcessStartedAt,
            },
            sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-voice-token", token);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Log.Warning("Command daemon rejected the token");
                return new VoiceCommandResult(VoiceCommandOutcome.NotReached,
                    "The command daemon rejected the token — check Settings");
            }

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning($"Command daemon returned {(int)response.StatusCode}");
                return new VoiceCommandResult(VoiceCommandOutcome.NotReached,
                    $"The command daemon returned {(int)response.StatusCode}");
            }

            return Read(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user cancelled the cycle (ESC). The request may still have landed.
            return new VoiceCommandResult(VoiceCommandOutcome.Uncertain, "Cancelled while sending");
        }
        catch (OperationCanceledException)
        {
            // HttpClient's own timeout. The daemon may be mid-command; never resend.
            Log.Warning("Command daemon did not answer in time");
            return new VoiceCommandResult(VoiceCommandOutcome.Uncertain,
                "No answer from the command daemon — it may still be running");
        }
        catch (HttpRequestException ex)
        {
            Log.Info($"Command daemon unreachable: {ex.Message}");
            return new VoiceCommandResult(VoiceCommandOutcome.NotReached,
                "No command daemon is listening");
        }
        catch (Exception ex)
        {
            Log.Error("Command dispatch failed", ex);
            return new VoiceCommandResult(VoiceCommandOutcome.NotReached, "Command dispatch failed");
        }
    }

    /// <summary>
    /// Watch one goal until it ends, so the pill can say what happened instead
    /// of leaving "working on it" on screen. Polling, not streaming: the daemon
    /// is a small local HTTP service and this costs nothing on loopback.
    /// </summary>
    public async Task FollowGoalAsync(
        string goalId,
        IProgress<VoiceGoalUpdate> progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(goalId)) return;
        var settings = _settingsService.Settings;
        var record = _resolve();
        var token = record?.Token ?? settings.CommandToken;
        var root = record is not null
            ? new Uri(new Uri(record.Url), "/goals/")
            : new Uri(new Uri(settings.CommandEndpoint), "/goals/");
        if (!IsLoopback(root.ToString())) return;

        var deadline = DateTime.UtcNow.AddMilliseconds(Constants.VoiceGoalFollowMaxMs);
        string? last = null;
        while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(Constants.VoiceGoalPollMs, cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, Uri.EscapeDataString(goalId)));
                request.Headers.TryAddWithoutValidation("x-voice-token", token);
                using var response = await _http.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) return;
                var update = ReadGoal(await response.Content.ReadAsStringAsync(cancellationToken));
                if (update is null) return;
                if (update.Status != last || update.Detail is not null)
                {
                    last = update.Status;
                    progress.Report(update);
                }
                if (update.Finished) return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // The goal is the daemon's; losing sight of it changes nothing
                // about whether it ran, so this never becomes an error here.
                Log.Info($"Goal {goalId} could not be followed: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>The goal document is data: only the fields we expect are read.</summary>
    internal static VoiceGoalUpdate? ReadGoal(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("goal", out var goal) || goal.ValueKind != JsonValueKind.Object) return null;
            var status = goal.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (string.IsNullOrWhiteSpace(status)) return null;
            var detail = goal.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            if (string.IsNullOrWhiteSpace(detail) && goal.TryGetProperty("question", out var q) && q.ValueKind == JsonValueKind.String)
                detail = q.GetString();
            if (detail is not null && detail.Length > Constants.VoiceCommandMessageMaxChars)
                detail = detail[..Constants.VoiceCommandMessageMaxChars].TrimEnd() + "…";
            var steps = goal.TryGetProperty("tools", out var t) && t.TryGetInt32(out var count) ? count : 0;
            return new VoiceGoalUpdate(status!, detail, steps);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Does whatever holds that port say it is the command daemon? Speech goes
    /// nowhere until something answers with the service's own name, because a
    /// configured port outlives the program that used to own it.
    /// </summary>
    private async Task<bool> AnswersAsTheDaemonAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            var health = new Uri(new Uri(endpoint), "/health");
            using var probe = new CancellationTokenSource(Constants.VoiceCommandHealthTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(probe.Token, cancellationToken);
            using var response = await _http.GetAsync(health, linked.Token);
            if (!response.IsSuccessStatusCode) return false;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(linked.Token));
            var service = document.RootElement.TryGetProperty("service", out var name) ? name.GetString() : null;
            if (service == VoiceEndpointResolver.ServiceName) return true;
            Log.Warning($"{endpoint} is answered by something else, so the command was not sent");
            return false;
        }
        catch (Exception ex)
        {
            Log.Info($"Command endpoint did not identify itself: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The daemon's answer is data, never instructions. Only the fields we expect are
    /// read, and the message is bounded so a long body cannot fill a toast.
    /// </summary>
    internal static VoiceCommandResult Read(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
            var commandId = root.TryGetProperty("commandId", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
            var outcome = root.TryGetProperty("outcome", out var o) ? o.GetString() : null;
            var ok = root.TryGetProperty("ok", out var k) && k.ValueKind == JsonValueKind.True;
            var goalId = root.TryGetProperty("goalId", out var g) && g.ValueKind == JsonValueKind.String
                ? g.GetString()
                : null;

            var message = !string.IsNullOrWhiteSpace(detail) ? detail! : outcome ?? "Command sent";
            if (message.Length > Constants.VoiceCommandMessageMaxChars)
                message = message[..Constants.VoiceCommandMessageMaxChars].TrimEnd() + "…";

            return new VoiceCommandResult(VoiceCommandOutcome.Delivered, message, commandId, ok, goalId);
        }
        catch (JsonException)
        {
            // It answered, so the command was received; we just cannot read the reply.
            return new VoiceCommandResult(VoiceCommandOutcome.Delivered, "Command sent");
        }
    }
}
