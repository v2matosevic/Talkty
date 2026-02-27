using Talkty.App.Models;

namespace Talkty.App.Services;

public interface IAudioCaptureService : IDisposable
{
    event EventHandler<float>? AudioLevelChanged;

    IReadOnlyList<AudioDevice> GetAvailableDevices();
    void SelectDevice(string? deviceId);
    void StartRecording();
    void StopRecording();
    byte[] GetRecordedAudio();
    float[] GetRecordedAudioAsFloat();
    bool IsRecording { get; }
}
