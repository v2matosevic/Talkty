using Talkty.App.Models;

namespace Talkty.App.Services;

public interface IAudioCaptureService : IDisposable
{
    event EventHandler<float>? AudioLevelChanged;

    IReadOnlyList<AudioDevice> GetAvailableDevices();
    void SelectDevice(string? deviceId);
    void StartRecording();
    void StopRecording();
    /// <summary>
    /// Stops recording and waits for NAudio's in-flight buffers to be fully delivered
    /// via the DataAvailable callback before returning. Without this, the last ~200-400ms
    /// of audio held in NAudio's internal buffers is lost.
    /// </summary>
    Task<bool> StopRecordingAndFlushAsync(int timeoutMs = 500);
    byte[] GetRecordedAudio();
    float[] GetRecordedAudioAsFloat();
    bool IsRecording { get; }
}
