using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Talkty.App.Models;
using Talkty.App.Services;
using Talkty.App.Services.Engines;
using Xunit;

namespace Talkty.Tests;

public class CloudRecoveryTests
{
    [Fact]
    public async Task DisposingDuringAnEarlyPassDoesNotBlockTheCallerOrAllowAnotherPass()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"Complete\"}") };
        }));
        var service = new TranscriptionService(() => new OpenRouterEngine(http));
        service.SetCloudApiKey("test-key");
        await service.LoadModelAsync(ModelProfile.CloudMaiTranscribe2, "");
        var pending = service.TranscribeEarlyAsync(new float[16000], "en", default, null, null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await Task.Run(service.Dispose).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(await service.EnsureModelLoadedAsync());
        }
        finally { release.TrySetResult(); }
        await pending;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.TranscribeAsync(new float[16000]));
    }

    [Fact]
    public async Task AnEarlyCloudFailureDoesNotRetryOrSpendOnTheBackup()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent("{}") });
        }));
        using var service = new TranscriptionService(() => new OpenRouterEngine(http));
        service.SetCloudApiKey("test-key");
        service.SetCloudFallback(ModelProfile.CloudQwen3Asr);
        await service.LoadModelAsync(ModelProfile.CloudMaiTranscribe2, "");
        var result = await service.TranscribeEarlyAsync(new float[16000], "en", default, null, null);
        Assert.False(result.Success);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(ModelProfile.CloudMaiTranscribe2, ModelProfile.CloudQwen3Asr, "opus", "mp3")]
    [InlineData(ModelProfile.CloudQwen3Asr, ModelProfile.CloudMaiTranscribe2, "mp3", "opus")]
    public async Task BothModelsFailOverWithoutRepeatingTheBusyProvider(ModelProfile primary, ModelProfile fallback,
        string primaryFormat, string fallbackFormat)
    {
        var models = new List<string>();
        var formats = new List<string>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var model = json.RootElement.GetProperty("model").GetString()!;
            models.Add(model);
            formats.Add(json.RootElement.GetProperty("input_audio").GetProperty("format").GetString()!);
            return new HttpResponseMessage(model == primary.GetOpenRouterModelId() ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
            { Content = new StringContent(model == primary.GetOpenRouterModelId() ? "{}" : "{\"text\":\"Recovered\"}") };
        }));
        using var service = new TranscriptionService(() => new OpenRouterEngine(http));
        service.SetCloudApiKey("test-key");
        service.SetCloudFallback(fallback);
        await service.LoadModelAsync(primary, "");
        var result = await service.TranscribeAsync(Enumerable.Repeat(0.1f, 16000).ToArray());
        Assert.True(result.Success);
        Assert.Equal(new[] { primary.GetOpenRouterModelId(), fallback.GetOpenRouterModelId() }, models);
        Assert.Equal(primaryFormat, formats[0] == "wav" ? primaryFormat : formats[0]); // Windows N has no MP3 encoder.
        Assert.Equal(fallbackFormat, formats[1] == "wav" ? fallbackFormat : formats[1]);
    }

    [Fact]
    public async Task ReusedPublicOptionsNeverReplayThePreviousAudio()
    {
        var audio = new List<string>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            audio.Add(json.RootElement.GetProperty("input_audio").GetProperty("data").GetString()!);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"Speech\"}") };
        }));
        using var engine = new OpenRouterEngine(http);
        engine.SetApiKey("test-key");
        await engine.LoadModelAsync(ModelProfile.CloudMaiTranscribe2, "");
        var options = new TranscriptionOptions();
        await engine.TranscribeAsync(new float[16000], options);
        await engine.TranscribeAsync(Enumerable.Range(0, 16000).Select(i => MathF.Sin(i * 0.1f)).ToArray(), options);
        Assert.NotEqual(audio[0], audio[1]);
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(503, true)]
    [InlineData(401, false)]
    [InlineData(402, false)]
    [InlineData(400, false)]
    public async Task RealCloudPipelineFallsBackOnlyForTemporaryFailures(int code, bool expected)
    {
        var models = new List<string>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var model = json.RootElement.GetProperty("model").GetString()!;
            models.Add(model);
            if (model == "qwen/qwen3-asr-1.7b")
            {
                Assert.False(json.RootElement.TryGetProperty("provider", out _));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"Recovered speech\"}") };
            }
            var response = new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("{\"error\":{\"message\":\"busy\"}}") };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        }));
        using var service = new TranscriptionService(() => new OpenRouterEngine(http));
        service.SetCloudApiKey("test-key");
        service.SetCloudFallback(ModelProfile.CloudQwen3Asr17B);
        await service.LoadModelAsync(ModelProfile.CloudMaiTranscribe2, "");
        var result = await service.TranscribeAsync([0.1f, -0.1f]);
        Assert.Equal(expected, result.Success);
        Assert.Equal(expected ? 2 : 1, models.Count);
        Assert.Equal(ModelProfile.CloudMaiTranscribe2, service.CurrentProfile);
        Assert.Equal(expected ? ModelProfile.CloudQwen3Asr17B : null, result.UsedFallback);
    }

    [Theory]
    [InlineData(null, "en")]
    [InlineData(ModelProfile.CloudMaiTranscribe2, "en")]
    [InlineData(ModelProfile.CloudQwen3Asr17B, "hr")]
    public async Task DisabledDuplicateOrUnsupportedBackupIsNotSent(ModelProfile? fallback, string language)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        }));
        using var service = new TranscriptionService(() => new OpenRouterEngine(http));
        service.SetCloudApiKey("test-key");
        service.SetCloudFallback(fallback);
        await service.LoadModelAsync(ModelProfile.CloudMaiTranscribe2, "");
        Assert.False((await service.TranscribeAsync([0.1f], language)).Success);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancellationNeverStartsBackup()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }));
        using var service = new TranscriptionService(() => new OpenRouterEngine(http));
        service.SetCloudApiKey("test-key");
        service.SetCloudFallback(ModelProfile.CloudQwen3Asr17B);
        await service.LoadModelAsync(ModelProfile.CloudMaiTranscribe2, "");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TranscribeAsync([0.1f], cancellationToken: cts.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RecoverySurvivesNewStoreAndIsEncrypted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Talkty-recovery-test-" + Guid.NewGuid());
        var store = new RecordingRecoveryStore(directory);
        var recording = new RecoverableRecording { Samples = [0.25f, -0.5f], Error = "Private recording", PromptMode = true, Language = "de" };
        try
        {
            store.Save(recording);
            var reloaded = Assert.Single(new RecordingRecoveryStore(directory).Load());
            Assert.Equal(recording.Samples, reloaded.Samples);
            Assert.Equal("de", reloaded.Language);
            Assert.True(reloaded.PromptMode);
            Assert.DoesNotContain("Private recording", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Directory.GetFiles(directory).Single())));
            store.Delete(recording.Id);
            Assert.Empty(store.Load());
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> run) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => run(request, cancellationToken);
    }
}

internal sealed class MemoryRecoveryStore : RecordingRecoveryStore
{
    public Dictionary<Guid, RecoverableRecording> Items { get; } = [];
    public bool FailSave { get; set; }
    public override void Save(RecoverableRecording recording)
    {
        if (FailSave) throw new IOException("Disk full");
        Items[recording.Id] = recording;
    }
    public override IReadOnlyList<RecoverableRecording> Load() => Items.Values.ToList();
    public override void Delete(Guid id) => Items.Remove(id);
}

public partial class TranscriptionFlowTests
{
    [Theory]
    [InlineData(380, 420)]
    [InlineData(420, 520)]
    public Task RecoveryScreenShowsPersistentErrorAndRetry(int width, int height) => ui.Run(async () =>
    {
        using var context = new Context();
        context.ViewModel.RecoverableRecordings.Add(new RecoverableRecording
        {
            Samples = new float[16000 * 9], Error = "Provider is busy. Recording saved for retry."
        });
        var surface = LoadPreview("Talkty.App/MainWindow.xaml");
        var host = new System.Windows.Window { Content = surface, DataContext = context.ViewModel };
        surface.DataContext = context.ViewModel;
        Layout(surface, width, height);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Layout(surface, width, height);
        var retry = Assert.Single(Descendants<System.Windows.Controls.Button>(surface), b => Equals(b.Content, "Retry"));
        Assert.Same(context.ViewModel.RetryRecordingCommand, retry.Command);
        Assert.Equal(System.Windows.Visibility.Visible, retry.Visibility);
        Assert.True(retry.ActualHeight >= 24);
        SavePreview(surface, width, height, $"recovery-{width}.png");
        host.Content = null;
        host.Close();
    });

    [Fact]
    public Task SettingsBackupSelectionPersistsThroughSaveAndApply() => ui.Run(() =>
    {
        using var context = new Context();
        // Initialize the shared production brushes used by the real Settings window.
        _ = LoadPreview("Talkty.App/MainWindow.xaml");
        System.Windows.Application.Current.Resources["InverseBooleanToVisibilityConverter"] = new Talkty.App.Converters.InverseBooleanToVisibilityConverter();
        System.Windows.Application.Current.Resources["EnumToBoolConverter"] = new Talkty.App.Converters.EnumToBoolConverter();
        System.Windows.Application.Current.Resources["HexColorBrushConverter"] = new Talkty.App.Converters.HexColorBrushConverter();
        System.Windows.Application.Current.Resources["NotNullToVisibilityConverter"] = new Talkty.App.Converters.NotNullToVisibilityConverter();
        System.Windows.Application.Current.Resources["EqualityConverter"] = new Talkty.App.Converters.EqualityConverter();
        System.Windows.Application.Current.Resources["PercentageWidthConverter"] = new Talkty.App.Converters.PercentageWidthConverter();
        System.Windows.Application.Current.Resources["LevelToWidthConverter"] = new Talkty.App.Converters.LevelToWidthConverter();
        var window = new Talkty.App.Views.SettingsWindow(context.Settings, context.Audio);
        var vm = (Talkty.App.ViewModels.SettingsViewModel)window.DataContext;
        ((System.Windows.Controls.RadioButton)window.FindName("NavCloud")).IsChecked = true;
        var surface = (System.Windows.FrameworkElement)window.Content;
        Layout(surface, 680, 560);
        var prompting = Descendants<System.Windows.Controls.CheckBox>(surface)
            .Single(c => c.Content as string == "Turn dictation into an agent prompt");
        Assert.False(prompting.IsChecked);
        prompting.IsChecked = true;
        Assert.True(vm.PromptingEnabled);
        SavePreview(surface, 680, 560, "cloud-backup-settings.png");
        Assert.Equal(ModelProfile.CloudQwen3Asr, vm.SelectedCloudFallback!.Profile);
        vm.SelectedCloudFallback = vm.CloudFallbackOptions.Single(o => o.Profile == ModelProfile.CloudWhisperLargeV3Turbo);
        ((System.Windows.Controls.RadioButton)window.FindName("NavBehavior")).IsChecked = true;
        Layout(surface, 680, 560);
        var pauseToggle = Descendants<System.Windows.Controls.CheckBox>(surface)
            .Single(c => c.Content as string == "Start transcription during pauses");
        Assert.True(pauseToggle.IsChecked);
        Assert.True(pauseToggle.ActualWidth > 200);
        SavePreview(surface, 680, 560, "pause-transcription-settings.png");
        pauseToggle.IsChecked = false;
        Assert.False(vm.TranscribeDuringPauses);
        vm.SettingsSaved += (_, settings) => context.ViewModel.ApplySettings(settings);
        vm.Save();
        Assert.False(context.Settings.Settings.TranscribeDuringPauses);
        Assert.True(context.Settings.Settings.PromptingEnabled);
        Assert.Equal(ModelProfile.CloudWhisperLargeV3Turbo, context.Settings.Settings.CloudFallbackModel);
        var json = JsonSerializer.Serialize(context.Settings.Settings);
        Assert.Equal(ModelProfile.CloudWhisperLargeV3Turbo, JsonSerializer.Deserialize<AppSettings>(json)!.CloudFallbackModel);
        Assert.Equal(ModelProfile.CloudQwen3Asr, JsonSerializer.Deserialize<AppSettings>("{}")!.CloudFallbackModel);
        return Task.CompletedTask;
    });

    [Fact]
    public Task FailedCloudRecordingSurvivesNextTakeAndRetriesWithoutAutoPaste() => ui.Run(async () =>
    {
        using var context = new Context(s => s.ModelProfile = ModelProfile.CloudMaiTranscribe2);
        context.Engine.Run = (_, _) => Task.FromResult(new TranscriptionResult { ErrorMessage = "Rate limited" });
        await context.Record();
        var first = Assert.Single(context.ViewModel.RecoverableRecordings);
        Assert.Single(context.Recovery.Items);
        Assert.Empty(context.ViewModel.History);
        await context.Record();
        Assert.Equal(2, context.ViewModel.RecoverableRecordings.Count);
        context.Engine.Run = null;
        await context.ViewModel.RetryRecordingCommand.ExecuteAsync(first);
        Assert.Single(context.ViewModel.History);
        Assert.Equal(first.Timestamp, context.ViewModel.History[0].Timestamp);
        Assert.Single(context.Recovery.Items);
        Assert.Single(context.ViewModel.RecoverableRecordings);
        Assert.Equal(0, context.Paste.Pasted);
        Assert.Equal("Build the feature.", context.Clipboard.Text);
    });

    [Fact]
    public Task DiskFailureKeepsMemoryCopyAndDoesNotUpload() => ui.Run(async () =>
    {
        using var context = new Context(s => s.ModelProfile = ModelProfile.CloudMaiTranscribe2);
        context.Recovery.FailSave = true;
        await context.Record();
        Assert.Equal(0, context.Engine.Calls);
        var recording = Assert.Single(context.ViewModel.RecoverableRecordings);
        context.Recovery.FailSave = false;
        await context.ViewModel.RetryRecordingCommand.ExecuteAsync(recording);
        Assert.Single(context.ViewModel.History);
        Assert.Empty(context.ViewModel.RecoverableRecordings);
    });

    [Fact]
    public Task HistoryWriteFailureKeepsRecoveryAudio() => ui.Run(async () =>
    {
        using var context = new Context(s => s.ModelProfile = ModelProfile.CloudMaiTranscribe2);
        context.Settings.OnSaveHistory = _ => throw new IOException("Disk full");
        await context.Record();
        Assert.Single(context.Recovery.Items);
        Assert.Single(context.ViewModel.RecoverableRecordings);
        Assert.Contains(context.Warnings, s => s.Contains("could not be saved to history"));
    });
}
