using NAudio.Wave;
using Talkty.App.Services;
using Xunit;

namespace Talkty.Tests;

public class AudioCaptureServiceTests
{
    [Fact]
    public void TailSnapshotIsBoundedAndIncludesFlushedSamplesWithoutChangingTheRecording()
    {
        var recorder = new FakeRecorder();
        using var service = new AudioCaptureService(_ => recorder);
        service.StartRecording();
        recorder.Emit(8192);
        recorder.Emit(16384);
        recorder.Emit(-16384);
        Assert.Equal(new[] { 0.5f, -0.5f }, service.GetRecordedAudioTail(2));
        Assert.Equal(3, service.GetRecordedAudioAsFloat().Length);
        Assert.Empty(service.GetRecordedAudioTail(0));
    }

    [Fact]
    public void NextRecordingDisposesPreviousDeviceBeforeOpeningAnother()
    {
        var first = new FakeRecorder();
        var second = new FakeRecorder();
        var created = 0;
        using var service = new AudioCaptureService(_ =>
        {
            if (created++ == 0) return first;
            Assert.True(first.Disposed);
            return second;
        });
        service.StartRecording();
        first.Emit(16384);
        service.StopRecording();
        service.StartRecording();
        second.Emit(-16384);
        Assert.Equal(new[] { -0.5f }, service.GetRecordedAudioAsFloat());
    }

    [Fact]
    public async Task RetiredCallbacksCannotContaminateOrCompleteTheNextRecording()
    {
        var first = new FakeRecorder();
        var second = new FakeRecorder();
        var queue = new Queue<FakeRecorder>([first, second]);
        using var service = new AudioCaptureService(_ => queue.Dequeue());
        service.StartRecording();
        var oldData = first.CaptureDataCallback();
        var oldStop = first.CaptureStopCallback();
        service.StopRecording();
        service.StartRecording();

        oldData();
        oldStop();
        Assert.True(service.IsRecording);
        Assert.Empty(service.GetRecordedAudioAsFloat());
        var flush = service.StopRecordingAndFlushAsync(5000);
        Assert.False(flush.IsCompleted);
        second.Emit(8192);
        second.Complete();
        Assert.True(await flush);
        Assert.Equal(new[] { 0.25f }, service.GetRecordedAudioAsFloat());
    }

    [Fact]
    public async Task FlushIncludesFinalAudioDeliveredAfterStop()
    {
        var recorder = new FakeRecorder();
        using var service = new AudioCaptureService(_ => recorder);
        service.StartRecording();
        recorder.Emit(16384);
        var flush = service.StopRecordingAndFlushAsync(5000);
        Assert.False(flush.IsCompleted);
        recorder.Emit(-16384);
        recorder.Complete();
        Assert.True(await flush);
        Assert.Equal(new[] { 0.5f, -0.5f }, service.GetRecordedAudioAsFloat());
    }

    [Fact]
    public async Task FlushCanFollowAnEarlierSynchronousStop()
    {
        var recorder = new FakeRecorder();
        using var service = new AudioCaptureService(_ => recorder);
        service.StartRecording();
        service.StopRecording();
        var flush = service.StopRecordingAndFlushAsync(5000);
        Assert.False(flush.IsCompleted);
        recorder.Complete();
        Assert.True(await flush);
        Assert.Equal(1, recorder.StopCalls);
    }

    [Fact]
    public async Task SpontaneousMicrophoneFailureIsNotReportedAsSuccessfulFlush()
    {
        var recorder = new FakeRecorder();
        using var service = new AudioCaptureService(_ => recorder);
        service.StartRecording();
        recorder.Complete(new InvalidOperationException("Device disconnected"));
        Assert.False(service.IsRecording);
        Assert.False(await service.StopRecordingAndFlushAsync());
    }

    [Fact]
    public void FailedStartReleasesDeviceAndAllowsRetry()
    {
        var failed = new FakeRecorder { StartError = new InvalidOperationException("Microphone busy") };
        var next = new FakeRecorder();
        var queue = new Queue<FakeRecorder>([failed, next]);
        using var service = new AudioCaptureService(_ => queue.Dequeue());
        Assert.Throws<InvalidOperationException>(service.StartRecording);
        Assert.True(failed.Disposed);
        Assert.False(service.IsRecording);
        service.StartRecording();
        Assert.True(service.IsRecording);
    }

    [Fact]
    public async Task StopExceptionCompletesFlushWithoutWaitingForTimeout()
    {
        var recorder = new FakeRecorder { StopError = new InvalidOperationException("Device gone") };
        using var service = new AudioCaptureService(_ => recorder);
        service.StartRecording();
        var flush = service.StopRecordingAndFlushAsync(5000);
        Assert.True(flush.IsCompleted);
        Assert.False(await flush);
    }

    [Fact]
    public async Task DisposingReleasesDeviceAndUnblocksFlush()
    {
        var recorder = new FakeRecorder();
        var service = new AudioCaptureService(_ => recorder);
        service.StartRecording();
        var flush = service.StopRecordingAndFlushAsync(5000);
        service.Dispose();
        Assert.False(await flush);
        Assert.True(recorder.Disposed);
        Assert.False(service.IsRecording);
        Assert.Throws<ObjectDisposedException>(service.StartRecording);
        service.Dispose();
        Assert.Equal(1, recorder.DisposeCalls);
    }

    [Fact]
    public async Task FlushTimeoutIsReportedAsFailure()
    {
        var recorder = new FakeRecorder();
        using var service = new AudioCaptureService(_ => recorder);
        service.StartRecording();
        Assert.False(await service.StopRecordingAndFlushAsync(0));
    }

    [Fact]
    public void UsesSelectedDeviceAndDoesNotOpenTwiceWhileRecording()
    {
        int? selected = null;
        var creates = 0;
        using var service = new AudioCaptureService(device =>
        {
            selected = device;
            creates++;
            return new FakeRecorder();
        });
        service.SelectDevice("3");
        service.StartRecording();
        service.StartRecording();
        Assert.Equal(3, selected);
        Assert.Equal(1, creates);
    }

    [Fact]
    public async Task FlushWithoutRecordingReturnsFalse()
    {
        using var service = new AudioCaptureService(_ => throw new InvalidOperationException());
        Assert.False(await service.StopRecordingAndFlushAsync());
    }

    private sealed class FakeRecorder : IWaveIn
    {
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public WaveFormat WaveFormat { get; set; } = new(16000, 16, 1);
        public Exception? StartError { get; init; }
        public Exception? StopError { get; init; }
        public int StopCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool Disposed => DisposeCalls > 0;

        public void StartRecording()
        {
            if (StartError != null) throw StartError;
        }
        public void StopRecording()
        {
            StopCalls++;
            if (StopError != null) throw StopError;
        }
        public void Emit(short sample)
        {
            var bytes = BitConverter.GetBytes(sample);
            DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, bytes.Length));
        }
        public void Complete(Exception? error = null) =>
            RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        public Action CaptureDataCallback()
        {
            var callback = DataAvailable;
            return () => callback?.Invoke(this, new WaveInEventArgs([0, 64], 2));
        }
        public Action CaptureStopCallback()
        {
            var callback = RecordingStopped;
            return () => callback?.Invoke(this, new StoppedEventArgs(null));
        }
        public void Dispose() => DisposeCalls++;
    }
}
