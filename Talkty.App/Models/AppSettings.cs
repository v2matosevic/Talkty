using System.Windows.Input;

namespace Talkty.App.Models;

public class AppSettings
{
    public ModelProfile ModelProfile { get; set; } = ModelProfile.Tiny;
    public string? SelectedMicrophoneId { get; set; }
    public bool CopyToClipboard { get; set; } = true;
    public bool AutoPaste { get; set; } = false;
    public bool ShowNotification { get; set; } = false;
    public string Language { get; set; } = "en";
    public bool AutoDetectLanguage { get; set; } = false;
    public string ModelsPath { get; set; } = string.Empty;

    /// <summary>
    /// Whether to use GPU acceleration for transcription.
    /// When true, uses CUDA/DirectML. When false, uses CPU.
    /// </summary>
    public bool UseGpu { get; set; } = false;

    /// <summary>
    /// Whether to lower system volume during recording to prevent audio interference.
    /// </summary>
    public bool DuckVolumeWhileRecording { get; set; } = false;

    /// <summary>
    /// The level to duck volume to during recording (0.05 to 1.0).
    /// Lower values = quieter. Default is 0.20 (20% of original).
    /// </summary>
    public float VolumeDuckLevel { get; set; } = 0.20f;

    // Hotkey settings
    public HotkeyModifiers HotkeyModifier { get; set; } = HotkeyModifiers.Alt;
    public Key HotkeyKey { get; set; } = Key.Q;

    // UX hint tracking - tracks which hints the user has seen
    public UserHints Hints { get; set; } = new();
}

public class UserHints
{
    public bool HasSeenTrayMinimizeHint { get; set; }
    public bool HasSeenFirstRecordingHint { get; set; }
    public bool HasSeenAutoPasteHint { get; set; }
    public bool HasSeenModelDownloadHint { get; set; }
    public int AppLaunchCount { get; set; }
}

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Ctrl = 0x0002,
    Shift = 0x0004,
    Win = 0x0008
}
