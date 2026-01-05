using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Talkty.App.Models;
using Talkty.App.Services;

namespace Talkty.App.ViewModels;

/// <summary>
/// Represents a language option for the language dropdown.
/// </summary>
public record LanguageOption(string Code, string Name)
{
    public override string ToString() => Name;
}

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly IModelDownloadService _downloadService;
    private bool _disposed;

    [ObservableProperty]
    private ModelProfile _selectedProfile;

    [ObservableProperty]
    private ObservableCollection<AudioDevice> _audioDevices = [];

    [ObservableProperty]
    private AudioDevice? _selectedAudioDevice;

    [ObservableProperty]
    private bool _copyToClipboard = true;

    [ObservableProperty]
    private bool _autoPaste;

    [ObservableProperty]
    private bool _showNotification;

    [ObservableProperty]
    private LanguageOption _selectedLanguage = new("auto", "Auto Detect");

    [ObservableProperty]
    private bool _autoDetectLanguage;

    [ObservableProperty]
    private bool _useGpu;

    [ObservableProperty]
    private bool _duckVolumeWhileRecording;

    [ObservableProperty]
    private int _volumeDuckPercent = 20; // 5-100 percent

    [ObservableProperty]
    private string _modelsPath = "";

    [ObservableProperty]
    private string _hotkeyText = "Alt + Q";

    [ObservableProperty]
    private HotkeyModifiers _hotkeyModifier = HotkeyModifiers.Alt;

    [ObservableProperty]
    private Key _hotkeyKey = Key.Q;

    [ObservableProperty]
    private bool _isRecordingHotkey;

    [ObservableProperty]
    private string _testStatus = "";

    [ObservableProperty]
    private float _testAudioLevel;

    [ObservableProperty]
    private bool _isTesting;

    // Download state
    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _downloadStatusText = "";

    [ObservableProperty]
    private string _downloadSpeedText = "";

    [ObservableProperty]
    private string _downloadTimeRemaining = "";

    // Model download status for each profile
    [ObservableProperty]
    private bool _tinyModelDownloaded;

    [ObservableProperty]
    private bool _baseModelDownloaded;

    [ObservableProperty]
    private bool _smallModelDownloaded;

    [ObservableProperty]
    private bool _mediumModelDownloaded;

    [ObservableProperty]
    private bool _largeModelDownloaded;

    [ObservableProperty]
    private bool _largeTurboModelDownloaded;

    [ObservableProperty]
    private bool _distilLargeV3ModelDownloaded;

    [ObservableProperty]
    private bool _senseVoiceModelDownloaded;

    public event EventHandler<AppSettings>? SettingsSaved;
    public event EventHandler? CloseRequested;

    // All available model profiles, organized by category
    public IReadOnlyList<ModelProfile> AvailableProfiles { get; } =
    [
        // Recommended models first
        ModelProfile.LargeTurbo,     // Best multilingual balance
        ModelProfile.DistilLargeV3,  // Fastest English
        ModelProfile.SenseVoice,     // Ultra fast, 50+ languages
        // Classic Whisper models
        ModelProfile.Tiny,
        ModelProfile.Base,
        ModelProfile.Small,
        ModelProfile.Medium,
        ModelProfile.Large
    ];

    public IReadOnlyList<LanguageOption> AvailableLanguages { get; } =
    [
        new("auto", "Auto Detect"),
        new("en", "English"),
        new("hr", "Croatian"),
        new("de", "German"),
        new("es", "Spanish"),
        new("fr", "French"),
        new("it", "Italian"),
        new("pt", "Portuguese"),
        new("ru", "Russian"),
        new("zh", "Chinese"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("nl", "Dutch"),
        new("pl", "Polish"),
        new("uk", "Ukrainian"),
        new("cs", "Czech"),
        new("sk", "Slovak"),
        new("hu", "Hungarian"),
        new("ro", "Romanian"),
        new("bg", "Bulgarian"),
        new("sr", "Serbian"),
        new("sl", "Slovenian")
    ];

    public SettingsViewModel(ISettingsService settingsService, IAudioCaptureService audioCaptureService)
    {
        _settingsService = settingsService;
        _audioCaptureService = audioCaptureService;
        _downloadService = new ModelDownloadService();

        _audioCaptureService.AudioLevelChanged += OnAudioLevelChanged;

        LoadSettings();
        LoadAudioDevices();
        RefreshModelStatus();
    }

    partial void OnSelectedProfileChanged(ModelProfile value)
    {
        // When profile changes, check if model needs download
        RefreshModelStatus();
        OnPropertyChanged(nameof(IsSelectedModelDownloaded));
    }

    public void RefreshModelStatus()
    {
        TinyModelDownloaded = _settingsService.ModelExists(ModelProfile.Tiny);
        BaseModelDownloaded = _settingsService.ModelExists(ModelProfile.Base);
        SmallModelDownloaded = _settingsService.ModelExists(ModelProfile.Small);
        MediumModelDownloaded = _settingsService.ModelExists(ModelProfile.Medium);
        LargeModelDownloaded = _settingsService.ModelExists(ModelProfile.Large);
        LargeTurboModelDownloaded = _settingsService.ModelExists(ModelProfile.LargeTurbo);
        DistilLargeV3ModelDownloaded = _settingsService.ModelExists(ModelProfile.DistilLargeV3);
        SenseVoiceModelDownloaded = _settingsService.ModelExists(ModelProfile.SenseVoice);
    }

    public bool IsSelectedModelDownloaded => SelectedProfile switch
    {
        ModelProfile.Tiny => TinyModelDownloaded,
        ModelProfile.Base => BaseModelDownloaded,
        ModelProfile.Small => SmallModelDownloaded,
        ModelProfile.Medium => MediumModelDownloaded,
        ModelProfile.Large => LargeModelDownloaded,
        ModelProfile.LargeTurbo => LargeTurboModelDownloaded,
        ModelProfile.DistilLargeV3 => DistilLargeV3ModelDownloaded,
        ModelProfile.SenseVoice => SenseVoiceModelDownloaded,
        _ => false
    };

    public bool AnyModelNotDownloaded =>
        !TinyModelDownloaded || !BaseModelDownloaded || !SmallModelDownloaded ||
        !MediumModelDownloaded || !LargeModelDownloaded || !LargeTurboModelDownloaded ||
        !DistilLargeV3ModelDownloaded || !SenseVoiceModelDownloaded;

    public string SelectedModelSizeDisplay => SelectedProfile.GetSizeDisplay();

    private void OnAudioLevelChanged(object? sender, float level)
    {
        if (IsTesting)
        {
            TestAudioLevel = level;
        }
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Settings;
        SelectedProfile = settings.ModelProfile;
        CopyToClipboard = settings.CopyToClipboard;
        AutoPaste = settings.AutoPaste;
        ShowNotification = settings.ShowNotification;

        // Find matching language option by code, default to "auto"
        SelectedLanguage = AvailableLanguages.FirstOrDefault(l => l.Code == settings.Language)
                          ?? AvailableLanguages.First();

        AutoDetectLanguage = settings.AutoDetectLanguage;
        UseGpu = settings.UseGpu;
        DuckVolumeWhileRecording = settings.DuckVolumeWhileRecording;
        VolumeDuckPercent = (int)(settings.VolumeDuckLevel * 100);
        ModelsPath = _settingsService.GetModelsDirectory();
        HotkeyModifier = settings.HotkeyModifier;
        HotkeyKey = settings.HotkeyKey;
        UpdateHotkeyText();
    }

    private void UpdateHotkeyText()
    {
        var parts = new List<string>();

        if (HotkeyModifier.HasFlag(HotkeyModifiers.Ctrl))
            parts.Add("Ctrl");
        if (HotkeyModifier.HasFlag(HotkeyModifiers.Alt))
            parts.Add("Alt");
        if (HotkeyModifier.HasFlag(HotkeyModifiers.Shift))
            parts.Add("Shift");
        if (HotkeyModifier.HasFlag(HotkeyModifiers.Win))
            parts.Add("Win");

        if (parts.Count == 0)
            parts.Add("None");

        parts.Add(HotkeyKey.ToString());

        HotkeyText = string.Join(" + ", parts);
    }

    public void SetHotkey(Key key, ModifierKeys modifiers)
    {
        // Convert WPF ModifierKeys to our HotkeyModifiers
        HotkeyModifiers mods = HotkeyModifiers.None;

        if (modifiers.HasFlag(ModifierKeys.Control))
            mods |= HotkeyModifiers.Ctrl;
        if (modifiers.HasFlag(ModifierKeys.Alt))
            mods |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift))
            mods |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows))
            mods |= HotkeyModifiers.Win;

        // Require at least one modifier
        if (mods == HotkeyModifiers.None)
        {
            Log.Warning("Hotkey must have at least one modifier (Ctrl, Alt, Shift, or Win)");
            return;
        }

        // Don't allow just modifier keys
        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin ||
            key == Key.System)
        {
            return;
        }

        HotkeyModifier = mods;
        HotkeyKey = key;
        UpdateHotkeyText();
        IsRecordingHotkey = false;

        Log.Info($"Hotkey set to: {HotkeyText}");
    }

    private void LoadAudioDevices()
    {
        AudioDevices.Clear();
        var devices = _audioCaptureService.GetAvailableDevices();

        foreach (var device in devices)
        {
            AudioDevices.Add(device);

            if (device.Id == _settingsService.Settings.SelectedMicrophoneId)
            {
                SelectedAudioDevice = device;
            }
        }

        if (SelectedAudioDevice == null && AudioDevices.Count > 0)
        {
            SelectedAudioDevice = AudioDevices[0];
        }
    }

    [RelayCommand]
    public async Task DownloadModel()
    {
        if (IsDownloading)
        {
            // Cancel current download
            _downloadService.CancelDownload();
            IsDownloading = false;
            DownloadStatusText = "Cancelled";
            return;
        }

        var profile = SelectedProfile;
        var destPath = _settingsService.GetModelPath(profile);

        IsDownloading = true;
        DownloadProgress = 0;
        DownloadStatusText = "Starting download...";
        DownloadSpeedText = "";
        DownloadTimeRemaining = "";

        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                DownloadProgress = p.Percentage;
                if (p.IsVerifying)
                {
                    DownloadStatusText = "Verifying integrity...";
                    DownloadSpeedText = "";
                    DownloadTimeRemaining = "";
                }
                else
                {
                    DownloadStatusText = p.DownloadedDisplay;
                    DownloadSpeedText = p.SpeedDisplay;
                    DownloadTimeRemaining = p.TimeRemainingDisplay;
                }
            });

            var success = await _downloadService.DownloadModelAsync(profile, destPath, progress);

            if (success)
            {
                DownloadStatusText = "Download complete!";
                RefreshModelStatus();
                OnPropertyChanged(nameof(IsSelectedModelDownloaded));
                OnPropertyChanged(nameof(AnyModelNotDownloaded));
            }
            else
            {
                DownloadStatusText = "Download cancelled";
            }
        }
        catch (Exception ex)
        {
            DownloadStatusText = $"Error: {ex.Message}";
            Log.Error("Model download failed", ex);
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    public void TestMicrophone()
    {
        if (IsTesting)
        {
            // Stop test
            StopMicrophoneTest();
            return;
        }

        if (SelectedAudioDevice == null)
        {
            TestStatus = "No microphone selected";
            return;
        }

        Log.Info($"Testing microphone: {SelectedAudioDevice.Name} (ID: {SelectedAudioDevice.Id})");
        TestStatus = $"Testing: {SelectedAudioDevice.Name}...";

        _audioCaptureService.SelectDevice(SelectedAudioDevice.Id);
        _audioCaptureService.StartRecording();
        IsTesting = true;
    }

    private void StopMicrophoneTest()
    {
        _audioCaptureService.StopRecording();
        IsTesting = false;

        var samples = _audioCaptureService.GetRecordedAudioAsFloat();
        var maxLevel = samples.Length > 0 ? samples.Max(Math.Abs) : 0;

        Log.Info($"Test complete. Samples: {samples.Length}, Max level: {maxLevel:F3}");

        if (maxLevel < 0.01f)
        {
            TestStatus = "No audio detected! Check microphone.";
        }
        else if (maxLevel < 0.1f)
        {
            TestStatus = $"Low audio level ({maxLevel:P0}). Speak louder or check mic.";
        }
        else
        {
            TestStatus = $"Microphone OK! Level: {maxLevel:P0}";
        }

        TestAudioLevel = 0;
        Log.Info("Microphone test stopped");
    }

    [RelayCommand]
    public void Save()
    {
        if (IsTesting)
        {
            StopMicrophoneTest();
        }

        var languageCode = SelectedLanguage?.Code ?? "auto";
        var settings = new AppSettings
        {
            ModelProfile = SelectedProfile,
            SelectedMicrophoneId = SelectedAudioDevice?.Id,
            CopyToClipboard = CopyToClipboard,
            AutoPaste = AutoPaste,
            ShowNotification = ShowNotification,
            Language = languageCode,
            AutoDetectLanguage = languageCode == "auto",
            UseGpu = UseGpu,
            DuckVolumeWhileRecording = DuckVolumeWhileRecording,
            VolumeDuckLevel = VolumeDuckPercent / 100f,
            ModelsPath = ModelsPath,
            HotkeyModifier = HotkeyModifier,
            HotkeyKey = HotkeyKey
        };

        SettingsSaved?.Invoke(this, settings);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void StartRecordingHotkey()
    {
        IsRecordingHotkey = true;
        HotkeyText = "Press keys...";
    }

    [RelayCommand]
    public void CancelRecordingHotkey()
    {
        IsRecordingHotkey = false;
        UpdateHotkeyText();
    }

    [RelayCommand]
    public void Cancel()
    {
        if (IsTesting)
        {
            _audioCaptureService.StopRecording();
            IsTesting = false;
        }

        if (IsDownloading)
        {
            _downloadService.CancelDownload();
            IsDownloading = false;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void OpenModelsFolder()
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", ModelsPath);
        }
        catch
        {
            // Ignore
        }
    }

    [RelayCommand]
    public void IncreaseVolumeDuckLevel()
    {
        VolumeDuckPercent = Math.Min(100, VolumeDuckPercent + 5);
    }

    [RelayCommand]
    public void DecreaseVolumeDuckLevel()
    {
        VolumeDuckPercent = Math.Max(5, VolumeDuckPercent - 5);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe from audio level events
        _audioCaptureService.AudioLevelChanged -= OnAudioLevelChanged;

        // Stop any ongoing mic test
        if (IsTesting)
        {
            _audioCaptureService.StopRecording();
            IsTesting = false;
        }

        // Cancel any ongoing download
        if (IsDownloading)
        {
            _downloadService.CancelDownload();
            IsDownloading = false;
        }

        GC.SuppressFinalize(this);
    }
}
