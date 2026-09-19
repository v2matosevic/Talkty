using System.Windows;
using System.Windows.Threading;
using Talkty.App.Models;
using Talkty.App.Services;
using Talkty.App.ViewModels;
using Xunit;

namespace Talkty.Tests;

// The real view-model pipeline runs on a WPF dispatcher, with no windows, microphone,
// model, network requests, system clipboard access, or persisted user settings.
public partial class TranscriptionFlowTests(UiThread ui) : IClassFixture<UiThread>
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

    [Fact]
    public Task RecordingPrewarmsTheCloudAndSendsVocabularyTerms() => ui.Run(async () =>
    {
        using var context = new Context();
        context.ViewModel.ApplySettings(new AppSettings
        {
            UseCustomVocabulary = true, Language = "hr", CustomVocabulary = [.. DefaultVocabulary.CodingTerms, "Revori"]
        });
        await context.Record();
        Assert.Equal(1, context.Engine.PrewarmCalls);
        var terms = context.Engine.LastVocabularyTerms!;
        Assert.Equal("Revori", terms[0]);
        Assert.Equal(Talkty.App.Constants.CloudMaxVocabularyTerms, terms.Count);
    });

    private sealed class Context : IDisposable
    {
        public FakeSettings Settings { get; } = new();
        public FakeAudio Audio { get; } = new();
        public FakeEngine Engine { get; } = new();
        public FakeClipboard Clipboard { get; } = new();
        public FakePaste Paste { get; } = new();
        public FakeRefiner Refiner { get; } = new();
        public FakeFidelity Fidelity { get; } = new();
        public FakeDecisions Decisions { get; } = new();
        public FakeVoiceCommand Voice { get; } = new();
        public MemoryRecoveryStore Recovery { get; } = new();
        public PromptClassifier Classifier { get; private set; } = null!;
        public MainViewModel ViewModel { get; }
        public List<string> Warnings { get; } = [];
        public List<string> Statuses { get; } = [];
        /// <summary>What the pill was told to show about a spoken command.</summary>
        public List<CommandProgressEventArgs> Pill { get; } = [];

        public Context(Action<AppSettings>? configure = null)
        {
            configure?.Invoke(Settings.Settings);
            Classifier = new PromptClassifier(Decisions);
            Classifier.SetApiKey("test-key");
            ViewModel = new MainViewModel(Settings, Audio, Engine, Clipboard,
                new FakeUpdate(), autoPasteService: Paste, promptRefinementService: Refiner, recoveryStore: Recovery,
                promptFidelityService: Fidelity, voiceCommandService: Voice,
                promptClassifier: Classifier);
            ViewModel.RequestShowToast += (_, e) => Warnings.Add(e.Message);
            ViewModel.CommandProgress += (_, e) => Pill.Add(e);
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

    internal sealed class FakeVoiceCommand : IVoiceCommandService
    {
        public bool IsConfigured { get; set; } = true;
        public List<string> Sent { get; } = [];
        public CapturedWindowInfo? Target { get; private set; }
        public VoiceCommandResult Result { get; set; } =
            new(VoiceCommandOutcome.Delivered, "recorded os.launch");

        public Task<VoiceCommandResult> DispatchAsync(string text, string? foregroundApp, CancellationToken ct)
        {
            Sent.Add(text);
            return Task.FromResult(Result);
        }
        public Task<VoiceCommandResult> DispatchAsync(string text, string? foregroundApp, CancellationToken ct, CapturedWindowInfo? target)
        {
            Target = target;
            return DispatchAsync(text, foregroundApp, ct);
        }

        /// <summary>Which goal the pill went on to watch, if any.</summary>
        public string? Followed { get; private set; }
        public List<VoiceGoalUpdate> Updates { get; } = [];

        public Task FollowGoalAsync(string goalId, IProgress<VoiceGoalUpdate> progress, CancellationToken ct)
        {
            Followed = goalId;
            foreach (var update in Updates) progress.Report(update);
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeSettings : ISettingsService
    {
        // A real encrypted key, because MainViewModel's startup forwards whatever settings hold to
        // the cloud services — an out-of-band SetApiKey on the classifier would just be overwritten.
        public AppSettings Settings { get; } = new()
        {
            AutoPaste = true,
            UseCustomVocabulary = false,
            OpenRouterApiKeyEncrypted = ApiKeyProtector.Protect("test-key")
        };
        public bool IsFirstRun => false;
        public void Load() { }
        public void Save() { }
        public string GetModelsDirectory() => "";
        public string GetModelPath(ModelProfile profile) => "";
        public bool ModelExists(ModelProfile profile) => false;
        public List<TranscriptionHistoryEntry> LoadHistory() => [];
        public Action<List<TranscriptionHistoryEntry>>? OnSaveHistory { get; set; }
        public void SaveHistory(List<TranscriptionHistoryEntry> history) => OnSaveHistory?.Invoke(history);
    }

    internal sealed class FakeAudio : IAudioCaptureService
    {
        public event EventHandler<float>? AudioLevelChanged;
        public void EmitLevel(float level) => AudioLevelChanged?.Invoke(this, level);
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
        public IReadOnlyList<string>? LastVocabularyTerms { get; private set; }
        public int PrewarmCalls { get; private set; }
        public Func<CancellationToken, Action<string>?, Task<TranscriptionResult>>? Run { get; set; }
        public Task<TranscriptionResult> TranscribeAsync(float[] audioSamples, string language = "en",
            CancellationToken cancellationToken = default, Action<string>? onFirstSegment = null,
            string? vocabularyPrompt = null, IReadOnlyList<string>? vocabularyTerms = null)
        {
            Calls++;
            LastVocabularyPrompt = vocabularyPrompt;
            LastVocabularyTerms = vocabularyTerms;
            if (Run != null) return Run(cancellationToken, onFirstSegment);
            onFirstSegment?.Invoke("First part");
            return Task.FromResult(new TranscriptionResult { Success = true, Text = Text });
        }
        public Task<bool> LoadModelAsync(ModelProfile profile, string modelPath, bool useGpu = false) => Task.FromResult(true);
        public Task<bool> EnsureModelLoadedAsync() => Task.FromResult(true);
        public void SetVocabularyPrompt(string? prompt) => VocabularyHint = prompt;
        public void SetCloudApiKey(string? apiKey) { }
        public void PrewarmCloud() => PrewarmCalls++;
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
        public CapturedWindowInfo? CapturedWindow { get; set; }
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
        public string? LastHint { get; private set; }
        public bool LastPreferredQualityModel { get; private set; }

        public Task<string?> RefineAsync(string transcription, CancellationToken cancellationToken = default,
            string? hint = null, bool preferQualityModel = false)
        {
            Calls++;
            LastHint = hint;
            LastPreferredQualityModel = preferQualityModel;
            return Run(cancellationToken);
        }
    }

    // Stands in for the Jev-backed fidelity check: records what it was handed, never touches a network.
    internal sealed class FakeFidelity : IPromptFidelityService
    {
        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PromptFidelityMode Mode { get; set; } = PromptFidelityMode.RecordOnly;
        public int Calls { get; private set; }
        public int Cancellations { get; private set; }
        public string? LastTranscript { get; private set; }
        public string? LastRewrite { get; private set; }
        public List<FidelityConcern> RaiseOnEvaluate { get; } = [];

        /// <summary>Completes once EvaluateAsync has been entered (the call is fire-and-forget).</summary>
        public Task Called => _called.Task;

        public event EventHandler<FidelityConcernEventArgs>? ConcernRaised;

        public void SetApiKey(string? apiKey) { }
        public void CancelPending() => Cancellations++;

        public Task<PromptFidelityOutcome> EvaluateAsync(string transcript, string rewrite)
        {
            Calls++;
            LastTranscript = transcript;
            LastRewrite = rewrite;
            if (RaiseOnEvaluate.Count > 0)
                ConcernRaised?.Invoke(this, new FidelityConcernEventArgs { Concerns = RaiseOnEvaluate.ToList() });
            _called.TrySetResult();
            return Task.FromResult(PromptFidelityOutcome.None(PromptFidelityStatus.CodeOnly));
        }
    }

    /// <summary>Answers the pre-refinement classifier from a script, with no network.</summary>
    internal sealed class FakeDecisions : IJevDecisionClient
    {
        public int Calls { get; private set; }
        public JevResult Result { get; set; } = JevResult.Fail(JevStatus.Unavailable, "timeout");

        public Task<JevResult> EvaluateAsync(string apiKey, System.Text.Json.Nodes.JsonNode state,
            IReadOnlyList<JevQuestion> questions, CancellationToken cancellationToken = default,
            int? timeoutMs = null)
        {
            Calls++;
            return Task.FromResult(Result);
        }

        /// <summary>A confident "this is already a prompt" answer.</summary>
        public void AnswerAlreadyAPrompt() => Result = Build(0.03, "chore", 0.95, 0.9, 0.1, 0.92);

        /// <summary>A confident "this needs organising" answer for substantial work.</summary>
        public void AnswerNeedsRefinement() => Result = Build(0.93, "feature", 0.9, 0.85, 1.9, 0.9);

        private static JevResult Build(double needsStructure, string kind, double kindP,
            double kindConfidence, double complexity, double complexityConfidence)
        {
            var kinds = new Dictionary<string, double>
            {
                ["bug"] = 0, ["feature"] = 0, ["refactor"] = 0, ["question"] = 0, ["chore"] = 0
            };
            kinds[kind] = kindP;
            kinds[kind == "chore" ? "bug" : "chore"] = 1 - kindP;

            var answers = new Dictionary<string, JevAnswer>
            {
                [PromptClassifier.NeedsStructureId] = new(PromptClassifier.NeedsStructureId, null, null, null, needsStructure),
                [PromptClassifier.RequestKindId] = new(PromptClassifier.RequestKindId, kind, kindConfidence, kinds, null),
                [PromptClassifier.ComplexityId] = new(PromptClassifier.ComplexityId, null, complexityConfidence,
                    new Dictionary<string, double> { ["0"] = 0.34, ["1"] = 0.33, ["2"] = 0.33 }, null, complexity),
            };
            return new JevResult(JevStatus.Evaluated,
                new JevEvaluation("typesafe/jev-1.13-20260917", answers, 420, 30, 0.00002, 480), null);
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
