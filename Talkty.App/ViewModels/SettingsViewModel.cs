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

    // Vocabulary
    [ObservableProperty]
    private bool _useCustomVocabulary = true;

    [ObservableProperty]
    private string _customVocabularyText = "";

    // Data-driven model collections
    public ObservableCollection<ModelProfileViewModel> LowEndModels { get; } = [];
    public ObservableCollection<ModelProfileViewModel> MidRangeModels { get; } = [];
    public ObservableCollection<ModelProfileViewModel> HighEndModels { get; } = [];
    private readonly List<ModelProfileViewModel> _allModels = [];

    public event EventHandler<AppSettings>? SettingsSaved;
    public event EventHandler? CloseRequested;

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

        InitializeModelProfiles();
        LoadSettings();
        LoadAudioDevices();
        RefreshModelStatus();
    }

    private void InitializeModelProfiles()
    {
        // Low-End PCs (4GB RAM)
        AddModel(LowEndModels, ModelProfile.Tiny, "Tiny", "English only, fastest (75 MB)");
        AddModel(LowEndModels, ModelProfile.Base, "Base", "English only, fast (142 MB)");

        // Mid-Range PCs (8GB RAM)
        AddModel(MidRangeModels, ModelProfile.Small, "Small", "English only, balanced (466 MB)");
        AddModel(MidRangeModels, ModelProfile.SmallQ5, "Small Lite", "English, quantized for CPU (190 MB)", "Quantized", "#06B6D4");
        AddModel(MidRangeModels, ModelProfile.DistilLargeV3, "Distil Large v3", "English only, fastest Whisper (756 MB)", "Best English", "#F59E0B");

        // High-End PCs (16GB+ RAM, GPU)
        AddModel(HighEndModels, ModelProfile.LargeTurbo, "Large v3 Turbo", "99+ languages, 6x faster than Large (1.6 GB)", "Recommended", "#8B5CF6");
        AddModel(HighEndModels, ModelProfile.Medium, "Medium", "English only, accurate (1.5 GB)");
        AddModel(HighEndModels, ModelProfile.MediumQ5, "Medium Lite", "English, quantized for CPU (539 MB)", "Quantized", "#06B6D4");
        AddModel(HighEndModels, ModelProfile.SenseVoice, "SenseVoice", "zh/en/ja/ko, 15x faster than Whisper (1 GB)", "Ultra Fast", "#10B981");
        AddModel(HighEndModels, ModelProfile.Large, "Large v3", "99+ languages, best accuracy (3.1 GB)");
        AddModel(HighEndModels, ModelProfile.LargeTurboQ5, "Large Turbo Lite", "Multilingual, quantized for CPU (574 MB)", "Quantized", "#06B6D4");
    }

    private void AddModel(ObservableCollection<ModelProfileViewModel> category, ModelProfile profile, string name, string description, string? badge = null, string badgeColor = "#8B5CF6")
    {
        var vm = new ModelProfileViewModel
        {
            Profile = profile,
            Name = name,
            Description = description,
            BadgeText = badge,
            BadgeColor = badgeColor
        };
        category.Add(vm);
        _allModels.Add(vm);
    }

    partial void OnSelectedProfileChanged(ModelProfile value)
    {
        // When profile changes, check if model needs download
        RefreshModelStatus();
        OnPropertyChanged(nameof(IsSelectedModelDownloaded));
    }

    public void RefreshModelStatus()
    {
        foreach (var model in _allModels)
        {
            model.IsDownloaded = _settingsService.ModelExists(model.Profile);
        }
    }

    public bool IsSelectedModelDownloaded =>
        _allModels.FirstOrDefault(m => m.Profile == SelectedProfile)?.IsDownloaded ?? false;

    public string SelectedModelSizeDisplay => SelectedProfile.GetSizeDisplay();

    [RelayCommand]
    public void SelectModel(ModelProfileViewModel? model)
    {
        if (model != null)
        {
            SelectedProfile = model.Profile;
        }
    }

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

        // Load vocabulary settings
        UseCustomVocabulary = settings.UseCustomVocabulary;
        if (settings.CustomVocabulary is { Count: > 0 })
        {
            CustomVocabularyText = string.Join(", ", settings.CustomVocabulary);
        }
        else
        {
            CustomVocabularyText = string.Join(", ", DefaultVocabulary.CodingTerms);
        }
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

        // Parse vocabulary text back to list
        List<string>? vocabularyList = null;
        if (!string.IsNullOrWhiteSpace(CustomVocabularyText))
        {
            vocabularyList = CustomVocabularyText
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }

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
            HotkeyKey = HotkeyKey,
            UseCustomVocabulary = UseCustomVocabulary,
            CustomVocabulary = vocabularyList
        };

        SettingsSaved?.Invoke(this, settings);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void ResetVocabularyToDefaults()
    {
        CustomVocabularyText = string.Join(", ", DefaultVocabulary.CodingTerms);
        UseCustomVocabulary = true;
        Log.Info("Vocabulary reset to defaults");
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
