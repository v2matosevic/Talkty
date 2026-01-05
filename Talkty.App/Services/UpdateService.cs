using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Talkty.App.Services;

public record UpdateInfo(
    string LatestVersion,
    string DownloadUrl,
    string ReleaseNotes,
    bool UpdateAvailable
);

public interface IUpdateService
{
    Task<UpdateInfo?> CheckForUpdatesAsync();
    string CurrentVersion { get; }
}

public class UpdateService : IUpdateService
{
    // Version check URL - points to version.json in the GitHub repo
    // Expected JSON format:
    // {
    //   "version": "1.0.5",
    //   "download_url": "https://github.com/v2matosevic/Talkty/releases/latest",
    //   "release_notes": "Bug fixes and improvements"
    // }
    private const string UpdateCheckUrl = "https://raw.githubusercontent.com/v2matosevic/Talkty/main/version.json";

    // Set to false to disable update checks entirely (useful during development)
    private const bool UpdateChecksEnabled = true;

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public string CurrentVersion { get; }

    public UpdateService()
    {
        // Get version from assembly
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        CurrentVersion = version != null
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "1.0.0";
    }

    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
#pragma warning disable CS0162 // Unreachable code detected - keep for development toggle
        if (!UpdateChecksEnabled)
        {
            Log.Debug("Update checks are disabled");
            return null;
        }
#pragma warning restore CS0162

        try
        {
            Log.Info($"Checking for updates... Current version: {CurrentVersion}");

            var response = await HttpClient.GetStringAsync(UpdateCheckUrl);
            var json = JsonDocument.Parse(response);
            var root = json.RootElement;

            var latestVersion = root.GetProperty("version").GetString() ?? CurrentVersion;
            var downloadUrl = root.GetProperty("download_url").GetString() ?? "";
            var releaseNotes = root.TryGetProperty("release_notes", out var notes)
                ? notes.GetString() ?? ""
                : "";

            var updateAvailable = IsNewerVersion(latestVersion, CurrentVersion);

            Log.Info($"Update check complete. Latest: {latestVersion}, Update available: {updateAvailable}");

            return new UpdateInfo(latestVersion, downloadUrl, releaseNotes, updateAvailable);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning($"Update check failed (network): {ex.Message}");
            return null;
        }
        catch (JsonException ex)
        {
            Log.Warning($"Update check failed (invalid response): {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning($"Update check failed: {ex.Message}");
            return null;
        }
    }

    private static bool IsNewerVersion(string latest, string current)
    {
        try
        {
            var latestParts = latest.Split('.').Select(int.Parse).ToArray();
            var currentParts = current.Split('.').Select(int.Parse).ToArray();

            // Pad arrays to same length
            var maxLength = Math.Max(latestParts.Length, currentParts.Length);
            Array.Resize(ref latestParts, maxLength);
            Array.Resize(ref currentParts, maxLength);

            for (int i = 0; i < maxLength; i++)
            {
                if (latestParts[i] > currentParts[i]) return true;
                if (latestParts[i] < currentParts[i]) return false;
            }

            return false; // Versions are equal
        }
        catch
        {
            return false;
        }
    }
}
