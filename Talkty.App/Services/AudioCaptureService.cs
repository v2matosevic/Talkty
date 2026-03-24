using System.Runtime.InteropServices;
using NAudio.Wave;
using Talkty.App.Models;

namespace Talkty.App.Services;

public class AudioCaptureService : IAudioCaptureService
{
    private WaveInEvent? _waveIn;
    // Float samples accumulated directly during recording — no WAV encode/decode round-trip
    private List<float> _floatSamples = [];
    private string? _selectedDeviceId;
    private readonly object _recordingLock = new();
    private readonly object _dataLock = new();

    public event EventHandler<float>? AudioLevelChanged;

    public bool IsRecording { get; private set; }

    public IReadOnlyList<AudioDevice> GetAvailableDevices()
    {
        Log.Debug("GetAvailableDevices called");
        var devices = new List<AudioDevice>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var capabilities = WaveInEvent.GetCapabilities(i);
            devices.Add(new AudioDevice(i.ToString(), capabilities.ProductName));
            Log.Debug($"  Device {i}: {capabilities.ProductName}");
        }
        Log.Info($"Found {devices.Count} audio input devices");
        return devices;
    }

    public void SelectDevice(string? deviceId)
    {
        Log.Info($"SelectDevice: {deviceId ?? "default"}");
        _selectedDeviceId = deviceId;
    }

    public void StartRecording()
    {
        lock (_recordingLock)
        {
            if (IsRecording)
            {
                Log.Warning("StartRecording called but already recording");
                return;
            }

            Log.Info("StartRecording");

            lock (_dataLock)
            {
                int deviceNumber = 0;
                if (!string.IsNullOrEmpty(_selectedDeviceId) && int.TryParse(_selectedDeviceId, out int parsedId))
                {
                    deviceNumber = parsedId;
                }

                Log.Debug($"Using device number: {deviceNumber}");

                _waveIn = new WaveInEvent
                {
                    DeviceNumber = deviceNumber,
                    WaveFormat = new WaveFormat(Constants.SampleRate, 16, 1) // 16kHz, 16-bit, mono for Whisper
                };

                Log.Debug($"WaveFormat: {_waveIn.WaveFormat.SampleRate}Hz, {_waveIn.WaveFormat.BitsPerSample}bit, {_waveIn.WaveFormat.Channels}ch");

                // Pre-allocate for 2 minutes of audio to avoid list resizing during recording
                _floatSamples = new List<float>(Constants.SampleRate * 120);
            }

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;

            _waveIn.StartRecording();
            IsRecording = true;
            Log.Info("Recording started successfully");
        }
    }

    public void StopRecording()
    {
        lock (_recordingLock)
        {
            if (!IsRecording)
            {
                Log.Warning("StopRecording called but not recording");
                return;
            }

            Log.Info("StopRecording");
            IsRecording = false;

            try
            {
                _waveIn?.StopRecording();
            }
            catch (Exception ex)
            {
                Log.Error("Error stopping recording", ex);
            }

            Log.Debug("Recording stopped");
        }
    }

    public byte[] GetRecordedAudio()
    {
        // Legacy method kept for interface compatibility — use GetRecordedAudioAsFloat() instead
        Log.Warning("GetRecordedAudio called — this path is unused, use GetRecordedAudioAsFloat()");
        return [];
    }

    public float[] GetRecordedAudioAsFloat()
    {
        Log.Debug("GetRecordedAudioAsFloat called");

        lock (_dataLock)
        {
            if (_floatSamples.Count == 0)
            {
                Log.Warning("GetRecordedAudioAsFloat: no samples recorded");
                return [];
            }

            var result = _floatSamples.ToArray();
            Log.Info($"Returning {result.Length} float samples ({result.Length / (double)Constants.SampleRate:F2}s)");
            return result;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            // Use MemoryMarshal to reinterpret bytes as shorts — zero-copy, no manual byte shifting
            var shorts = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded));

            float max = 0;
            lock (_dataLock)
            {
                foreach (var s in shorts)
                {
                    var f = s / 32768f;
                    _floatSamples.Add(f);
                    var abs = Math.Abs(f);
                    if (abs > max) max = abs;
                }
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
        Log.Debug("OnRecordingStopped event");
        if (e.Exception != null)
        {
            Log.Error("Recording stopped with exception", e.Exception);
        }
    }

    public void Dispose()
    {
        Log.Debug("AudioCaptureService.Dispose");

        lock (_recordingLock)
        {
            if (_waveIn != null)
            {
                _waveIn.DataAvailable -= OnDataAvailable;
                _waveIn.RecordingStopped -= OnRecordingStopped;
                _waveIn.Dispose();
                _waveIn = null;
            }
        }

        GC.SuppressFinalize(this);
    }
}
