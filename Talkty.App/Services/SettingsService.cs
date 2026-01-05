using System.IO;
using System.Text.Json;
using Talkty.App.Models;

namespace Talkty.App.Services;

public class SettingsService : ISettingsService
{
    private static readonly string AppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Talkty");

    private static readonly string SettingsFilePath = Path.Combine(AppDataPath, "settings.json");
    private static readonly string HistoryFilePath = Path.Combine(AppDataPath, "history.json");
    private static readonly string DefaultModelsPath = Path.Combine(AppDataPath, "Models");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AppSettings Settings { get; private set; } = new();
    public bool IsFirstRun { get; private set; }

    public void Load()
    {
        Log.Info("Loading settings...");

        try
        {
            EnsureDirectoriesExist();
            IsFirstRun = !File.Exists(SettingsFilePath);

            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                Log.Info($"Settings loaded from {SettingsFilePath}");
            }
            else
            {
                Log.Info("No settings file found, using defaults (first run)");
                Settings = new AppSettings();
            }

            if (string.IsNullOrEmpty(Settings.ModelsPath))
            {
                Settings.ModelsPath = DefaultModelsPath;
            }
        }
        catch (JsonException ex)
        {
            Log.Error($"Failed to parse settings JSON: {ex.Message}", ex);
            Settings = new AppSettings { ModelsPath = DefaultModelsPath };
        }
        catch (IOException ex)
        {
            Log.Error($"Failed to read settings file: {ex.Message}", ex);
            Settings = new AppSettings { ModelsPath = DefaultModelsPath };
        }
        catch (Exception ex)
        {
            Log.Error($"Unexpected error loading settings: {ex.Message}", ex);
            Settings = new AppSettings { ModelsPath = DefaultModelsPath };
        }
    }

    public void Save()
    {
        Log.Debug("Saving settings...");

        try
        {
            EnsureDirectoriesExist();
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
            Log.Info($"Settings saved to {SettingsFilePath}");
        }
        catch (IOException ex)
        {
            Log.Error($"Failed to write settings file: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            Log.Error($"Unexpected error saving settings: {ex.Message}", ex);
        }
    }

    public List<TranscriptionHistoryEntry> LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryFilePath))
            {
                var json = File.ReadAllText(HistoryFilePath);
                var history = JsonSerializer.Deserialize<List<TranscriptionHistoryEntry>>(json, JsonOptions);
                Log.Info($"Loaded {history?.Count ?? 0} history entries");
                return history ?? [];
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load history: {ex.Message}", ex);
        }

        return [];
    }

    public void SaveHistory(List<TranscriptionHistoryEntry> history)
    {
        try
        {
            EnsureDirectoriesExist();
            var json = JsonSerializer.Serialize(history, JsonOptions);
            File.WriteAllText(HistoryFilePath, json);
            Log.Debug($"Saved {history.Count} history entries");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to save history: {ex.Message}", ex);
        }
    }

    public string GetModelsDirectory() => Settings.ModelsPath;

    public string GetModelPath(ModelProfile profile)
    {
        return Path.Combine(GetModelsDirectory(), profile.GetModelFileName());
    }

    public bool ModelExists(ModelProfile profile)
    {
        var path = GetModelPath(profile);

        // SherpaOnnx models are directories (extracted from archives)
        if (profile.GetEngine() == Models.TranscriptionEngine.SherpaOnnx)
        {
            return Directory.Exists(path);
        }

        // Whisper models are single files
        return File.Exists(path);
    }

    private void EnsureDirectoriesExist()
    {
        try
        {
            if (!Directory.Exists(AppDataPath))
            {
                Directory.CreateDirectory(AppDataPath);
                Log.Debug($"Created app data directory: {AppDataPath}");
            }

            if (!Directory.Exists(Settings.ModelsPath) && !string.IsNullOrEmpty(Settings.ModelsPath))
            {
                Directory.CreateDirectory(Settings.ModelsPath);
                Log.Debug($"Created models directory: {Settings.ModelsPath}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to create directories: {ex.Message}", ex);
        }
    }
}

public class TranscriptionHistoryEntry
{
    public string Text { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public double DurationSeconds { get; set; }
}
