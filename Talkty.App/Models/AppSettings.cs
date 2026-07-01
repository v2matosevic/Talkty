using System.Windows.Input;

namespace Talkty.App.Models;

public class AppSettings
{
    public ModelProfile ModelProfile { get; set; } = ModelProfile.Tiny;
    public string? SelectedMicrophoneId { get; set; }
    public bool CopyToClipboard { get; set; } = true;
    public bool AutoPaste { get; set; } = false;
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

    /// <summary>
    /// Unload the local speech model after ~15 minutes without a transcription to free
    /// RAM/VRAM while the app idles in the tray. It reloads transparently on the next
    /// recording (the reload overlaps with speaking, so it's rarely felt).
    /// </summary>
    public bool UnloadModelWhenIdle { get; set; } = true;

    /// <summary>
    /// Whether to use the custom vocabulary prompt during transcription.
    /// </summary>
    public bool UseCustomVocabulary { get; set; } = true;

    /// <summary>
    /// Custom vocabulary terms passed to Whisper's initial_prompt to bias
    /// transcription toward correct spelling of technical jargon.
    /// </summary>
    public List<string>? CustomVocabulary { get; set; }

    /// <summary>
    /// Post-transcription text replacements for words Whisper consistently misrecognizes.
    /// Key = misheard text (case-insensitive), Value = correct replacement.
    /// Applied deterministically after transcription for 100% reliability.
    /// </summary>
    public Dictionary<string, string>? TextReplacements { get; set; }

    /// <summary>
    /// OpenRouter API key for cloud transcription, stored ENCRYPTED (Windows DPAPI, CurrentUser).
    /// Never holds plaintext on disk. Encrypt via <c>ApiKeyProtector.Protect</c> before assigning;
    /// decrypt via <c>ApiKeyProtector.Unprotect</c> at point of use.
    /// </summary>
    public string? OpenRouterApiKeyEncrypted { get; set; }

    /// <summary>
    /// OpenRouter model slug the "Prompting" feature uses to expand dictation into a coding-agent
    /// prompt. User-selectable in Settings; defaults to the fastest, lowest-latency option (the
    /// completeness guard auto-escalates to a stronger model if it ever summarizes, so speed is the
    /// right default). The refiner keeps the remaining built-in models as automatic fallbacks
    /// regardless of this choice.
    /// </summary>
    public string PromptingModel { get; set; } = "google/gemini-3.1-flash-lite";

    // Hotkey settings
    public HotkeyModifiers HotkeyModifier { get; set; } = HotkeyModifiers.Alt;
    public Key HotkeyKey { get; set; } = Key.Q;

    // UX hint tracking - tracks which hints the user has seen
    public UserHints Hints { get; set; } = new();
}

public class UserHints
{
    public bool HasSeenTrayMinimizeHint { get; set; }
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
