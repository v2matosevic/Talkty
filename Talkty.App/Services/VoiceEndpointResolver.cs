using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;

namespace Talkty.App.Services;

/// <summary>
/// Where the local command daemon actually is.
///
/// A port number is not an identity: while the daemon was down, another program
/// took its port and started receiving spoken commands. So the daemon writes a
/// small record when it starts, and this reads it:
///
///   %LOCALAPPDATA%\Version2\services\hermes-voice.json
///   { service, pid, url, command, token, ... }
///
/// The record is believed only when it names this service, points at this
/// machine, and its process is still alive. Anything else is ignored, and the
/// caller falls back to the configured endpoint, which it checks separately.
/// </summary>
public static class VoiceEndpointResolver
{
    public const string ServiceName = "hermes-voice";

    public record Endpoint(string Url, string? Token, int Pid);

    public static string ServicesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Version2", "services");

    public static string RecordPath => Path.Combine(ServicesDirectory, ServiceName + ".json");

    /// <summary>The daemon's command URL, or null when no live record says where it is.</summary>
    public static Endpoint? Read(string? path = null, Func<int, bool>? isAlive = null)
    {
        try
        {
            var file = path ?? RecordPath;
            if (!File.Exists(file)) return null;
            return Parse(File.ReadAllText(file), isAlive ?? ProcessIsAlive);
        }
        catch (Exception ex)
        {
            Log.Info($"Voice service record could not be read: {ex.Message}");
            return null;
        }
    }

    internal static Endpoint? Parse(string json, Func<int, bool> isAlive)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("service", out var service) || service.GetString() != ServiceName) return null;
        if (!root.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String) return null;
        if (!root.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out var processId)) return null;
        if (!isAlive(processId)) return null;

        var command = root.TryGetProperty("command", out var path) && path.ValueKind == JsonValueKind.String
            ? path.GetString()!
            : "/command";
        if (!Uri.TryCreate(new Uri(url.GetString()!), command, out var full)) return null;
        if (!IsLoopback(full)) return null;

        var token = root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        return new Endpoint(full.ToString(), token, processId);
    }

    /// <summary>A record whose process has gone is a leftover, not an address.</summary>
    public static bool ProcessIsAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    internal static bool IsLoopback(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.IsLoopback) return true;
        return IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip);
    }
}
