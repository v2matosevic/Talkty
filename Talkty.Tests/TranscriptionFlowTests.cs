using System.Windows;
using System.Windows.Threading;
using Talkty.App.Models;
using Talkty.App.Services;
using Talkty.App.ViewModels;
using Xunit;

namespace Talkty.Tests;

// The real view-model pipeline runs on a WPF dispatcher, with no windows, microphone,
// model, network requests, system clipboard access, or persisted user settings.
public class TranscriptionFlowTests(UiThread ui) : IClassFixture<UiThread>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CancelDuringRefinementNeverCopiesOrPastes(bool returnsText) => ui.Run(async () =>
    {
        using var context = new Context();
        context.Refiner.Run = _ =>
        {
            context.ViewModel.CancelRecording();
            return Task.FromResult(returnsText ? "A generated prompt" : null);
        };
        await context.ViewModel.StartListeningAsync();
        context.ViewModel.PromptMode = true;
        await context.ViewModel.StopListeningAndTranscribeAsync();
        Assert.Equal("Original clipboard", context.Clipboard.Text);
        Assert.Empty(context.Clipboard.Writes);
        Assert.Equal(0, context.Paste.Pasted);
        Assert.Empty(context.ViewModel.History);
        Assert.Empty(context.Warnings);
        Assert.False(context.ViewModel.IsTranscribing);
        Assert.Equal("Cancelled", context.ViewModel.StatusText);
    });

    [Fact]
    public Task CancelDuringFlushNeverStartsTranscription() => ui.Run(async () =>
    {
        using var context = new Context();
        var flushed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Audio.Flush = () => flushed.Task;
        await context.ViewModel.StartListeningAsync();
        var operation = context.ViewModel.StopListeningAndTranscribeAsync();
        context.ViewModel.CancelRecording();
        flushed.SetResult(true);
        await operation;
        Assert.Equal(0, context.Engine.Calls);
        Assert.Empty(context.Clipboard.Writes);
        Assert.Empty(context.Warnings);
        Assert.False(context.ViewModel.IsTranscribing);
    });

    [Fact]
    public Task EngineReturningSuccessAfterCancelCannotPaste() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Engine.Run = (_, _) =>
        {
            context.ViewModel.CancelRecording();
            return Task.FromResult(new TranscriptionResult { Success = true, Text = "Cancelled speech" });
        };
        await context.Record();
        Assert.Empty(context.Clipboard.Writes);
        Assert.Equal(0, context.Paste.Pasted);
        Assert.Empty(context.ViewModel.History);
        Assert.False(context.ViewModel.IsTranscribing);
    });

    [Fact]
    public Task HallucinationOnlyResultNeverClearsClipboardOrCallsRefiner() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Engine.Text = "[MUSIC]";
        await context.ViewModel.StartListeningAsync();
        context.ViewModel.PromptMode = true;
        await context.ViewModel.StopListeningAndTranscribeAsync();
        Assert.Empty(context.Clipboard.Writes);
        Assert.Equal(0, context.Refiner.Calls);
        Assert.Empty(context.ViewModel.History);
        Assert.Contains(context.Warnings, message => message.Contains("No speech"));
        Assert.False(context.ViewModel.IsTranscribing);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(16000)]
    public Task EmptyAndDigitallySilentRecordingsSkipInference(int samples) => ui.Run(async () =>
    {
        using var context = new Context();
        context.Audio.Samples = new float[samples];
        await context.Record();
        Assert.Equal(0, context.Engine.Calls);
        Assert.Empty(context.Clipboard.Writes);
        Assert.Single(context.Warnings);
        Assert.False(context.ViewModel.IsTranscribing);
    });

    [Fact]
    public Task VeryQuietAudioStillReachesTheEngine() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Audio.Samples = Enumerable.Repeat(1f / 32768, 16000).ToArray();
        await context.Record();
        Assert.Equal(1, context.Engine.Calls);
        Assert.Equal(1, context.Paste.Pasted);
    });

    [Fact]
    public Task FlushFailureWarnsAndPreservesUsableSpeech() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Audio.Flush = () => Task.FromResult(false);
        await context.Record();
        Assert.Contains(context.Warnings, message => message.Contains("end of this recording"));
        Assert.Single(context.ViewModel.History);
        Assert.Equal(1, context.Engine.Calls);
    });

    [Fact]
    public Task ClipboardFailureKeepsHistoryAndNeverClaimsCopied() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Clipboard.AcceptWrite = _ => false;
        await context.Record();
        Assert.Single(context.ViewModel.History);
        Assert.Equal(0, context.Paste.Pasted);
        Assert.DoesNotContain("Copied to clipboard", context.Statuses);
        Assert.Contains(context.Warnings, message => message.Contains("saved in History"));
    });

    [Fact]
    public Task ClipboardLossDuringFocusRestoreDoesNotPasteStaleContent() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Clipboard.AcceptWrite = count => count == 1;
        await context.Record();
        Assert.Equal(0, context.Paste.Pasted);
        Assert.Single(context.ViewModel.History);
        Assert.Contains(context.Warnings, message => message.Contains("became unavailable before paste"));
    });

    [Fact]
    public Task HistoryOnlyModeNeverClaimsClipboardSuccess() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Settings.Settings.CopyToClipboard = false;
        await context.Record();
        Assert.Empty(context.Clipboard.Writes);
        Assert.Single(context.ViewModel.History);
        Assert.Contains("Saved to history", context.Statuses);
        Assert.DoesNotContain("Copied to clipboard", context.Statuses);
    });

    [Fact]
    public Task SuccessfulPromptKeepsOriginalSpeechAndPastesOnlyThePrompt() => ui.Run(async () =>
    {
        using var context = new Context();
        await context.ViewModel.StartListeningAsync();
        context.ViewModel.PromptMode = true;
        await context.ViewModel.StopListeningAndTranscribeAsync();
        var history = Assert.Single(context.ViewModel.History);
        Assert.Equal("Build the feature.", history.RawTranscription);
        Assert.Equal("A generated prompt", history.Text);
        Assert.All(context.Clipboard.Writes, text => Assert.Equal("A generated prompt", text));
        Assert.Equal(1, context.Paste.Pasted);
    });

    [Fact]
    public Task ClipboardOnlyModeRetainsEarlyCleanedSegment() => ui.Run(async () =>
    {
        using var context = new Context();
        context.Settings.Settings.AutoPaste = false;
        await context.Record();
        Assert.Equal(new[] { "First part.", "Build the feature." }, context.Clipboard.Writes);
        Assert.Equal(0, context.Paste.Pasted);
    });

    [Fact]
    public Task ManualHistoryCopyFailureIsVisible() => ui.Run(() =>
    {
        using var context = new Context();
        context.Clipboard.AcceptWrite = _ => false;
        context.ViewModel.CopyHistoryItem(new TranscriptionHistoryItem { Text = "Saved speech" });
        Assert.Equal("Could not copy", context.ViewModel.StatusText);
        Assert.Single(context.Warnings);
        return Task.CompletedTask;
    });

    [Fact]
    public Task SavedVocabularyReachesStartupAndTheActualRecording() => ui.Run(async () =>
    {
        using var context = new Context(settings =>
        {
            settings.UseCustomVocabulary = true;
            settings.CustomVocabulary = [.. DefaultVocabulary.CodingTerms, "QuillForge"];
        });
        var startupHint = context.Engine.VocabularyHint;
        Assert.StartsWith("QuillForge, ", startupHint);
        await context.Record();
        Assert.Equal(startupHint, context.Engine.LastVocabularyPrompt);
    });

    [Fact]
    public Task VocabularyEditsAndDisableReachNextRecordingAndReloadHint() => ui.Run(async () =>
    {
        using var context = new Context();
        var updated = new AppSettings { UseCustomVocabulary = true, CustomVocabulary = ["QuillForge"] };
        context.ViewModel.ApplySettings(updated);
        Assert.Equal("QuillForge", context.Engine.VocabularyHint);
        await context.Record();
        Assert.Equal("QuillForge", context.Engine.LastVocabularyPrompt);

        updated.CustomVocabulary = ["Northstar"];
        context.ViewModel.ApplySettings(updated);
        await context.Record();
        Assert.Equal("Northstar", context.Engine.LastVocabularyPrompt);
        Assert.Equal("Northstar", context.Engine.VocabularyHint);

        updated.UseCustomVocabulary = false;
        context.ViewModel.ApplySettings(updated);
        await context.Record();
        Assert.Null(context.Engine.LastVocabularyPrompt);
        Assert.Null(context.Engine.VocabularyHint);
    });

    [Theory]
    [InlineData("hr", false)]
    [InlineData("en", true)]
    public Task LanguageChangeClearsEnglishHint(string language, bool auto) => ui.Run(async () =>
    {
        using var context = new Context(settings =>
        {
            settings.UseCustomVocabulary = true;
            settings.CustomVocabulary = ["QuillForge"];
        });
        context.ViewModel.ApplySettings(new AppSettings
        {
            UseCustomVocabulary = true, CustomVocabulary = ["QuillForge"],
            Language = language, AutoDetectLanguage = auto
        });
        await context.Record();
        Assert.Null(context.Engine.VocabularyHint);
        Assert.Null(context.Engine.LastVocabularyPrompt);
    });

    private sealed class Context : IDisposable
    {
        public FakeSettings Settings { get; } = new();
        public FakeAudio Audio { get; } = new();
        public FakeEngine Engine { get; } = new();
        public FakeClipboard Clipboard { get; } = new();
        public FakePaste Paste { get; } = new();
        public FakeRefiner Refiner { get; } = new();
        public MainViewModel ViewModel { get; }
        public List<string> Warnings { get; } = [];
        public List<string> Statuses { get; } = [];

        public Context(Action<AppSettings>? configure = null)
        {
            configure?.Invoke(Settings.Settings);
            ViewModel = new MainViewModel(Settings, Audio, Engine, Clipboard,
                new FakeUpdate(), autoPasteService: Paste, promptRefinementService: Refiner);
            ViewModel.RequestShowToast += (_, e) => Warnings.Add(e.Message);
            ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ViewModel.StatusText)) Statuses.Add(ViewModel.StatusText);
            };
        }
        public async Task Record()
        {
            await ViewModel.StartListeningAsync();
            await ViewModel.StopListeningAndTranscribeAsync();
        }
        public void Dispose() => ViewModel.Dispose();
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings Settings { get; } = new() { AutoPaste = true, UseCustomVocabulary = false };
        public bool IsFirstRun => false;
        public void Load() { }
        public void Save() { }
        public string GetModelsDirectory() => "";
        public string GetModelPath(ModelProfile profile) => "";
        public bool ModelExists(ModelProfile profile) => false;
        public List<TranscriptionHistoryEntry> LoadHistory() => [];
        public void SaveHistory(List<TranscriptionHistoryEntry> history) { }
    }

    private sealed class FakeAudio : IAudioCaptureService
    {
        public event EventHandler<float>? AudioLevelChanged { add { } remove { } }
        public bool IsRecording { get; private set; }
        public float[] Samples { get; set; } = [0.1f, -0.1f];
        public Func<Task<bool>> Flush { get; set; } = () => Task.FromResult(true);
        public IReadOnlyList<AudioDevice> GetAvailableDevices() => [];
        public void SelectDevice(string? deviceId) { }
        public void StartRecording() => IsRecording = true;
        public void StopRecording() => IsRecording = false;
        public Task<bool> StopRecordingAndFlushAsync(int timeoutMs = 500)
        {
            IsRecording = false;
            return Flush();
        }
        public byte[] GetRecordedAudio() => [];
        public float[] GetRecordedAudioAsFloat() => Samples;
        public void Dispose() { }
    }

    private sealed class FakeEngine : ITranscriptionService
    {
        public bool IsModelLoaded => true;
        public ModelProfile? CurrentProfile => ModelProfile.Tiny;
        public string? BackendInfo => "Test";
        public IReadOnlyList<string> SupportedLanguages => ["en"];
        public int Calls { get; private set; }
        public string Text { get; set; } = "Build the feature";
        public string? VocabularyHint { get; private set; }
        public string? LastVocabularyPrompt { get; private set; }
        public Func<CancellationToken, Action<string>?, Task<TranscriptionResult>>? Run { get; set; }
        public Task<TranscriptionResult> TranscribeAsync(float[] audioSamples, string language = "en",
            CancellationToken cancellationToken = default, Action<string>? onFirstSegment = null,
            string? vocabularyPrompt = null)
        {
            Calls++;
            LastVocabularyPrompt = vocabularyPrompt;
            if (Run != null) return Run(cancellationToken, onFirstSegment);
            onFirstSegment?.Invoke("First part");
            return Task.FromResult(new TranscriptionResult { Success = true, Text = Text });
        }
        public Task<bool> LoadModelAsync(ModelProfile profile, string modelPath, bool useGpu = false) => Task.FromResult(true);
        public Task<bool> EnsureModelLoadedAsync() => Task.FromResult(true);
        public void SetVocabularyPrompt(string? prompt) => VocabularyHint = prompt;
        public void SetCloudApiKey(string? apiKey) { }
        public void SetLanguageHint(string? language) { }
        public void SetIdleUnload(bool enabled) { }
        public void Dispose() { }
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public string Text { get; private set; } = "Original clipboard";
        public List<string> Writes { get; } = [];
        public Func<int, bool> AcceptWrite { get; set; } = _ => true;
        public bool SetText(string text)
        {
            Writes.Add(text);
            if (!AcceptWrite(Writes.Count)) return false;
            Text = text;
            return true;
        }
        public string? GetTextOrNull() => Text;
    }

    private sealed class FakePaste : IAutoPasteService
    {
        public int Pasted { get; private set; }
        public void CaptureTargetWindow() { }
        public void ClaimForegroundPrivilege() { }
        public PasteOutcome PasteToTargetWindow(Action? ensureClipboardText = null, CancellationToken cancellationToken = default)
        {
            try
            {
                return AutoPasteService.CommitPaste(() =>
                {
                    Pasted++;
                    return true;
                }, ensureClipboardText, cancellationToken) ? PasteOutcome.Pasted : PasteOutcome.Failed;
            }
            catch { return PasteOutcome.Failed; }
        }
    }

    private sealed class FakeRefiner : IPromptRefinementService
    {
        public bool IsConfigured => true;
        public string? LastError => null;
        public int Calls { get; private set; }
        public Func<CancellationToken, Task<string?>> Run { get; set; } = _ => Task.FromResult<string?>("A generated prompt");
        public void SetApiKey(string? apiKey) { }
        public void SetModel(string? modelSlug) { }
        public Task<string?> RefineAsync(string transcription, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Run(cancellationToken);
        }
    }

    private sealed class FakeUpdate : IUpdateService
    {
        public string CurrentVersion => "test";
        public Task<UpdateInfo?> CheckForUpdatesAsync() => Task.FromResult<UpdateInfo?>(null);
    }
}

public sealed class UiThread : IDisposable
{
    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;

    public UiThread()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _dispatcher = ready.Task.GetAwaiter().GetResult();
    }

    public Task Run(Func<Task> action) =>
        _dispatcher.InvokeAsync(action).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(10));

    public void Dispose()
    {
        _dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
