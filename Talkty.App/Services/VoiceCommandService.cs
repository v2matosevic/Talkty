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

    public VoiceCommandService(ISettingsService settingsService, HttpClient? http = null)
    {
        _settingsService = settingsService;
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromMilliseconds(Constants.VoiceCommandTimeoutMs);
    }

    public bool IsConfigured
    {
        get
        {
            var s = _settingsService.Settings;
            return !string.IsNullOrWhiteSpace(s.CommandEndpoint)
                   && !string.IsNullOrWhiteSpace(s.CommandToken)
                   && IsLoopback(s.CommandEndpoint);
        }
    }

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

        if (!IsLoopback(settings.CommandEndpoint))
        {
            Log.Warning($"Command endpoint is not on this machine: {settings.CommandEndpoint}");
            return new VoiceCommandResult(VoiceCommandOutcome.NotReached,
                "Command endpoint must be a local address");
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

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.CommandEndpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-voice-token", settings.CommandToken);

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

            var message = !string.IsNullOrWhiteSpace(detail) ? detail! : outcome ?? "Command sent";
            if (message.Length > Constants.VoiceCommandMessageMaxChars)
                message = message[..Constants.VoiceCommandMessageMaxChars].TrimEnd() + "…";

            return new VoiceCommandResult(VoiceCommandOutcome.Delivered, message, commandId);
        }
        catch (JsonException)
        {
            // It answered, so the command was received; we just cannot read the reply.
            return new VoiceCommandResult(VoiceCommandOutcome.Delivered, "Command sent");
        }
    }
}
