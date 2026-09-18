using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Talkty.App.Services;

public partial class RecoverableRecording : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public float[] Samples { get; set; } = [];
    public string Language { get; set; } = "en";
    public bool PromptMode { get; set; }
    [ObservableProperty] private string _error = "Recording saved. Retry when ready.";
    public string Label => $"{Timestamp:g} · {Samples.Length / 16000d:F1}s";
}

/// <summary>Atomic, user-encrypted recovery copies. Only successful or explicitly discarded
/// recordings are removed; new recordings never overwrite an earlier failed take.</summary>
public class RecordingRecoveryStore
{
    private readonly string _directory;
    public RecordingRecoveryStore(string? directory = null) => _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Talkty", "Recovery");
    private string FilePath(Guid id) => Path.Combine(_directory, $"{id:N}.recording");

    public virtual void Save(RecoverableRecording recording)
    {
        Directory.CreateDirectory(_directory);
        var encrypted = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(recording), null, DataProtectionScope.CurrentUser);
        var path = FilePath(recording.Id);
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(encrypted);
            stream.Flush(flushToDisk: true);
        }
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public virtual IReadOnlyList<RecoverableRecording> Load()
    {
        if (!Directory.Exists(_directory)) return [];
        var recordings = new List<RecoverableRecording>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.recording"))
        {
            try
            {
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                var recording = JsonSerializer.Deserialize<RecoverableRecording>(bytes);
                if (recording != null && recording.Samples.Length > 0) recordings.Add(recording);
            }
            catch (Exception ex) { Log.Error("Could not read a saved recording; file retained", ex); }
        }
        return recordings.OrderByDescending(r => r.Timestamp).ToList();
    }

    public virtual void Delete(Guid id) => File.Delete(FilePath(id));
}
