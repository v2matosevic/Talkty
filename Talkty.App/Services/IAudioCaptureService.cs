using Talkty.App.Models;

namespace Talkty.App.Services;

public interface IAudioCaptureService : IDisposable
{
    event EventHandler<float>? AudioLevelChanged;
    event EventHandler<byte[]>? AudioDataAvailable;

    IReadOnlyList<AudioDevice> GetAvailableDevices();
    void SelectDevice(string? deviceId);
    void StartRecording();
    void StopRecording();
    byte[] GetRecordedAudio();
    float[] GetRecordedAudioAsFloat();
    bool IsRecording { get; }
}
