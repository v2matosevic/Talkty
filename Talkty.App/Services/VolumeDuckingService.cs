using NAudio.CoreAudioApi;

namespace Talkty.App.Services;

/// <summary>
/// Smoothly lowers and restores system volume during recording sessions.
/// Uses Windows Core Audio API via NAudio.
/// </summary>
public class VolumeDuckingService : IVolumeDuckingService
{
    private const int FadeSteps = 10;
    private const int FadeStepDelayMs = 25; // Total ~250ms
    private const float DefaultDuckRatio = 0.20f;  // Duck to 20% of original (80% reduction)

    private readonly object _lock = new();

    private float _originalVolume;
    private float _duckLevel = DefaultDuckRatio; // Configurable duck level
    private bool _isDucked;
    private bool _hasStoredOriginalVolume; // Track if we captured original volume (for interrupted fades)
    private bool _disposed;
    private CancellationTokenSource? _fadeCts;

    public bool IsDucked
    {
        get { lock (_lock) return _isDucked; }
    }

    /// <summary>
    /// Gets or sets the duck level (0.05 to 1.0). Lower values = quieter ducked volume.
    /// </summary>
    public float DuckLevel
    {
        get { lock (_lock) return _duckLevel; }
        set { lock (_lock) _duckLevel = Math.Clamp(value, 0.05f, 1.0f); }
    }

    public VolumeDuckingService()
    {
        Log.Debug("VolumeDuckingService initialized");
    }

    public async Task DuckAsync()
    {
        CancellationToken ct;
        float duckLevel;

        lock (_lock)
        {
            if (_disposed)
            {
                Log.Warning("DuckAsync called on disposed service");
                return;
            }

            if (_isDucked)
            {
                Log.Debug("Already ducked, skipping");
                return;
            }

            // Cancel any ongoing fade
            _fadeCts?.Cancel();
            _fadeCts = new CancellationTokenSource();
            ct = _fadeCts.Token;
            duckLevel = _duckLevel;
        }

        try
        {
            // Create fresh COM objects for each operation to avoid stale references
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            var volume = device.AudioEndpointVolume;
            _originalVolume = volume.MasterVolumeLevelScalar;

            // Mark that we've stored the original volume (important for interrupted fades)
            lock (_lock) _hasStoredOriginalVolume = true;

            if (_originalVolume <= 0.01f)
            {
                Log.Debug($"Volume already very low ({_originalVolume:P0}), marking as ducked but not changing");
                lock (_lock) _isDucked = true;
                return;
            }

            var targetVolume = _originalVolume * duckLevel;
            Log.Info($"Ducking volume: {_originalVolume:P0} -> {targetVolume:P0} (duck level: {duckLevel:P0})");

            await FadeVolumeAsync(enumerator, _originalVolume, targetVolume, ct);

            lock (_lock) _isDucked = true;
            Log.Debug("Volume ducked successfully");
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Duck operation cancelled");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to duck volume", ex);
        }
    }

    public async Task RestoreAsync()
    {
        CancellationToken ct;
        float originalVolume;

        lock (_lock)
        {
            if (_disposed)
            {
                Log.Warning("RestoreAsync called on disposed service");
                return;
            }

            // Check if we ever stored an original volume (handles interrupted fades)
            if (!_hasStoredOriginalVolume)
            {
                Log.Debug("No original volume stored, skipping restore");
                return;
            }

            // Cancel any ongoing fade
            _fadeCts?.Cancel();
            _fadeCts = new CancellationTokenSource();
            ct = _fadeCts.Token;
            originalVolume = _originalVolume;
        }

        try
        {
            // Create fresh COM objects for each operation to avoid stale references
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            var volume = device.AudioEndpointVolume;
            var currentVolume = volume.MasterVolumeLevelScalar;

            Log.Info($"Restoring volume: {currentVolume:P0} -> {originalVolume:P0}");

            await FadeVolumeAsync(enumerator, currentVolume, originalVolume, ct);

            lock (_lock)
            {
                _isDucked = false;
                _hasStoredOriginalVolume = false;
            }
            Log.Debug("Volume restored successfully");
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Restore operation cancelled");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to restore volume", ex);
            // Try one more time with instant restore (no fade)
            try
            {
                Log.Info("Attempting instant volume restore after fade failure...");
                using var retryEnumerator = new MMDeviceEnumerator();
                var retryDevice = retryEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                retryDevice.AudioEndpointVolume.MasterVolumeLevelScalar = originalVolume;
                Log.Info($"Volume instantly restored to {originalVolume:P0}");
            }
            catch (Exception retryEx)
            {
                Log.Error("Failed to restore volume on retry", retryEx);
            }

            lock (_lock)
            {
                _isDucked = false;
                _hasStoredOriginalVolume = false;
            }
        }
    }

    private static async Task FadeVolumeAsync(
        MMDeviceEnumerator enumerator,
        float fromLevel,
        float toLevel,
        CancellationToken ct)
    {
        for (int i = 1; i <= FadeSteps; i++)
        {
            ct.ThrowIfCancellationRequested();

            var progress = (float)i / FadeSteps;
            var level = Lerp(fromLevel, toLevel, progress);

            // Get fresh device reference for each step to avoid stale COM objects
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(level, 0f, 1f);

            if (i < FadeSteps)
            {
                await Task.Delay(FadeStepDelayMs, ct);
            }
        }
    }

    private static float Lerp(float from, float to, float t)
    {
        return from + (to - from) * t;
    }

    public void Dispose()
    {
        bool needsRestore;
        float originalVolume;

        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            needsRestore = _hasStoredOriginalVolume; // Restore if we ever ducked (even if interrupted)
            originalVolume = _originalVolume;
        }

        // Restore volume synchronously on dispose to ensure cleanup
        if (needsRestore)
        {
            Log.Info("Disposing while ducked, restoring volume synchronously");
            try
            {
                _fadeCts?.Cancel();

                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                device.AudioEndpointVolume.MasterVolumeLevelScalar = originalVolume;
                Log.Info($"Volume restored to {originalVolume:P0} on dispose");
            }
            catch (Exception ex)
            {
                Log.Error("Failed to restore volume on dispose", ex);
            }
        }

        _fadeCts?.Dispose();
        Log.Debug("VolumeDuckingService disposed");

        GC.SuppressFinalize(this);
    }
}
