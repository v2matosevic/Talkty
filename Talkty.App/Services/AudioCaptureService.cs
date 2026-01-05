using System.IO;
using NAudio.Wave;
using Talkty.App.Models;

namespace Talkty.App.Services;

public class AudioCaptureService : IAudioCaptureService
{
    private WaveInEvent? _waveIn;
    private MemoryStream? _audioStream;
    private WaveFileWriter? _waveWriter;
    private string? _selectedDeviceId;
    private readonly object _recordingLock = new();
    private readonly object _dataLock = new();

    public event EventHandler<float>? AudioLevelChanged;
    public event EventHandler<byte[]>? AudioDataAvailable;

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
                _audioStream = new MemoryStream();

                int deviceNumber = 0;
                if (!string.IsNullOrEmpty(_selectedDeviceId) && int.TryParse(_selectedDeviceId, out int parsedId))
                {
                    deviceNumber = parsedId;
                }

                Log.Debug($"Using device number: {deviceNumber}");

                _waveIn = new WaveInEvent
                {
                    DeviceNumber = deviceNumber,
                    WaveFormat = new WaveFormat(16000, 16, 1) // 16kHz, 16-bit, mono for Whisper
                };

                Log.Debug($"WaveFormat: {_waveIn.WaveFormat.SampleRate}Hz, {_waveIn.WaveFormat.BitsPerSample}bit, {_waveIn.WaveFormat.Channels}ch");

                _waveWriter = new WaveFileWriter(_audioStream, _waveIn.WaveFormat);
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
        Log.Debug("GetRecordedAudio called");

        lock (_dataLock)
        {
            if (_audioStream == null)
            {
                Log.Warning("GetRecordedAudio: no audio stream");
                return [];
            }

            try
            {
                _waveWriter?.Flush();
                var bytes = _audioStream.ToArray();
                Log.Debug($"GetRecordedAudio: {bytes.Length} bytes");
                return bytes;
            }
            catch (Exception ex)
            {
                Log.Error("Error getting recorded audio", ex);
                return [];
            }
        }
    }

    public float[] GetRecordedAudioAsFloat()
    {
        Log.Debug("GetRecordedAudioAsFloat called");

        byte[] wavBytes;
        lock (_dataLock)
        {
            if (_audioStream == null)
            {
                Log.Warning("GetRecordedAudioAsFloat: no audio stream");
                return [];
            }

            try
            {
                _waveWriter?.Flush();
                wavBytes = _audioStream.ToArray();
            }
            catch (Exception ex)
            {
                Log.Error("Error getting audio bytes", ex);
                return [];
            }
        }

        if (wavBytes.Length == 0)
        {
            Log.Warning("GetRecordedAudioAsFloat: empty WAV bytes");
            return [];
        }

        Log.Debug($"Converting {wavBytes.Length} WAV bytes to float samples");

        try
        {
            using var ms = new MemoryStream(wavBytes);
            using var reader = new WaveFileReader(ms);

            var samples = new List<float>();
            var buffer = new byte[reader.WaveFormat.BlockAlign];

            while (reader.Read(buffer, 0, buffer.Length) > 0)
            {
                short sample = BitConverter.ToInt16(buffer, 0);
                samples.Add(sample / 32768f);
            }

            Log.Info($"Converted to {samples.Count} float samples ({samples.Count / 16000.0:F2}s)");
            return [.. samples];
        }
        catch (Exception ex)
        {
            Log.Error("Error converting audio to float", ex);
            return [];
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Don't lock here - just write data quickly
        try
        {
            lock (_dataLock)
            {
                _waveWriter?.Write(e.Buffer, 0, e.BytesRecorded);
            }

            // Calculate audio level for visualization (no lock needed)
            float max = 0;
            for (int i = 0; i < e.BytesRecorded; i += 2)
            {
                short sample = (short)(e.Buffer[i + 1] << 8 | e.Buffer[i]);
                float absValue = Math.Abs(sample / 32768f);
                if (absValue > max) max = absValue;
            }

            AudioLevelChanged?.Invoke(this, max);
            AudioDataAvailable?.Invoke(this, e.Buffer[..e.BytesRecorded]);
        }
        catch
        {
            // Ignore errors during recording
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        Log.Debug("OnRecordingStopped event");
        if (e.Exception != null)
        {
            Log.Error("Recording stopped with exception", e.Exception);
        }

        lock (_dataLock)
        {
            try
            {
                _waveWriter?.Flush();
            }
            catch
            {
                // Ignore
            }
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

        lock (_dataLock)
        {
            _waveWriter?.Dispose();
            _waveWriter = null;

            _audioStream?.Dispose();
            _audioStream = null;
        }

        GC.SuppressFinalize(this);
    }
}
