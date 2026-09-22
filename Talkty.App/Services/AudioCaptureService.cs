using System.Runtime.InteropServices;
using NAudio.Wave;
using Talkty.App.Models;

namespace Talkty.App.Services;

public class AudioCaptureService : IAudioCaptureService
{
    private IWaveIn? _waveIn;
    private readonly Func<int, IWaveIn> _createRecorder;
    private List<float> _floatSamples = [];
    private float[] _conversionBuffer = new float[4096];
    private string? _selectedDeviceId;
    private readonly object _recordingLock = new();
    private readonly object _dataLock = new();
    private TaskCompletionSource<bool>? _stopCompletion;
    private bool _disposed;
    private volatile bool _isRecording;

    public event EventHandler<float>? AudioLevelChanged;
    public bool IsRecording => _isRecording;

    public AudioCaptureService() : this(deviceNumber => new WaveInEvent
    {
        DeviceNumber = deviceNumber,
        WaveFormat = new WaveFormat(Constants.SampleRate, 16, 1)
    }) { }

    internal AudioCaptureService(Func<int, IWaveIn> createRecorder)
    {
        _createRecorder = createRecorder;
    }

    public IReadOnlyList<AudioDevice> GetAvailableDevices()
    {
        var devices = new List<AudioDevice>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var capabilities = WaveInEvent.GetCapabilities(i);
            devices.Add(new AudioDevice(i.ToString(), capabilities.ProductName));
        }
        Log.Info($"Found {devices.Count} audio input devices");
        return devices;
    }

    public void SelectDevice(string? deviceId)
    {
        lock (_recordingLock) { _selectedDeviceId = deviceId; }
    }

    public void StartRecording()
    {
        lock (_recordingLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRecording) return;

            // Retire the old device before opening another, including after cancellation.
            ReleaseRecorder();
            int deviceNumber = int.TryParse(_selectedDeviceId, out var selected) ? selected : 0;
            Log.Debug($"Opening microphone device {deviceNumber}");
            var recorder = _createRecorder(deviceNumber);
            lock (_dataLock)
            {
                _waveIn = recorder;
                // Grow for longer dictations instead of reserving 7.7 MB for every clip.
                _floatSamples = new List<float>(Constants.SampleRate * 15);
                _stopCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                recorder.DataAvailable += OnDataAvailable;
                recorder.RecordingStopped += OnRecordingStopped;
                _isRecording = true;
            }

            try
            {
                recorder.StartRecording();
                Log.Info("Recording started successfully");
            }
            catch
            {
                _isRecording = false;
                ReleaseRecorder();
                throw;
            }
        }
    }

    public void StopRecording()
    {
        lock (_recordingLock) { StopRecorder(); }
    }

    // Caller holds _recordingLock. Stop merely signals the worker; it is not a flush.
    private void StopRecorder()
    {
        if (!IsRecording) return;
        Log.Debug("Stopping recording; final buffers are still pending");
        _isRecording = false;
        try
        {
            _waveIn?.StopRecording();
        }
        catch (Exception ex)
        {
            Log.Error("Error stopping recording", ex);
            _stopCompletion?.TrySetResult(false);
        }
    }

    public async Task<bool> StopRecordingAndFlushAsync(int timeoutMs = 500)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutMs);
        Task<bool>? completion;
        lock (_recordingLock)
        {
            // Also await an earlier StopRecording or a spontaneous device stop.
            completion = _stopCompletion?.Task;
            StopRecorder();
        }
        if (completion == null) return false;

        try
        {
            return await completion.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warning($"Recording flush timed out after {timeoutMs}ms; last audio may be truncated");
            return false;
        }
    }

    public byte[] GetRecordedAudio()
    {
        Log.Warning("GetRecordedAudio is unused; use GetRecordedAudioAsFloat");
        return [];
    }

    public float[] GetRecordedAudioAsFloat()
    {
        lock (_dataLock)
        {
            Log.Debug($"Returning {_floatSamples.Count} recorded samples");
            return _floatSamples.ToArray();
        }
    }

    public float[] GetRecordedAudioTail(int maximumSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumSamples);
        lock (_dataLock)
        {
            var count = Math.Min(maximumSamples, _floatSamples.Count);
            return CollectionsMarshal.AsSpan(_floatSamples).Slice(_floatSamples.Count - count, count).ToArray();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            float max = 0;
            lock (_dataLock)
            {
                // A retired device may already have queued a callback. It must never
                // append to the next recording or race on its conversion buffer.
                if (!ReferenceEquals(sender, _waveIn)) return;
                var shorts = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded));
                if (_conversionBuffer.Length < shorts.Length)
                    _conversionBuffer = new float[shorts.Length];

                for (int i = 0; i < shorts.Length; i++)
                {
                    var sample = shorts[i] / 32768f;
                    _conversionBuffer[i] = sample;
                    max = Math.Max(max, Math.Abs(sample));
                }
                // Keep accepting the current device's final buffers after StopRecording.
                _floatSamples.AddRange(_conversionBuffer.AsSpan(0, shorts.Length));
            }
            AudioLevelChanged?.Invoke(this, max);
        }
        catch (Exception ex)
        {
            Log.Warning($"Error in audio data callback: {ex.Message}");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_dataLock)
        {
            if (!ReferenceEquals(sender, _waveIn)) return;
            _isRecording = false;
            if (e.Exception != null)
                Log.Error("Recording stopped with exception", e.Exception);
            // NAudio raises this only after delivering all buffered audio.
            _stopCompletion?.TrySetResult(e.Exception == null);
        }
    }

    // Caller holds _recordingLock. Never dispose under _dataLock: the driver may
    // wait for a callback that needs that lock before releasing the device.
    private void ReleaseRecorder()
    {
        IWaveIn? recorder;
        lock (_dataLock)
        {
            recorder = _waveIn;
            _waveIn = null;
            _stopCompletion?.TrySetResult(false);
            if (recorder != null)
            {
                recorder.DataAvailable -= OnDataAvailable;
                recorder.RecordingStopped -= OnRecordingStopped;
            }
        }
        recorder?.Dispose();
    }

    public void Dispose()
    {
        lock (_recordingLock)
        {
            if (_disposed) return;
            _disposed = true;
            _isRecording = false;
            ReleaseRecorder();
            lock (_dataLock) { _floatSamples = []; }
        }
        GC.SuppressFinalize(this);
    }
}
