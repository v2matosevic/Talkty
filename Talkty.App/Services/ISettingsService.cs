using Talkty.App.Models;

namespace Talkty.App.Services;

public interface ISettingsService
{
    AppSettings Settings { get; }
    bool IsFirstRun { get; }
    void Load();
    void Save();
    string GetModelsDirectory();
    string GetModelPath(ModelProfile profile);
    bool ModelExists(ModelProfile profile);
    List<TranscriptionHistoryEntry> LoadHistory();
    void SaveHistory(List<TranscriptionHistoryEntry> history);
}
