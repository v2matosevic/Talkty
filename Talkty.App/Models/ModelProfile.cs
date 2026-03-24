namespace Talkty.App.Models;

/// <summary>
/// Available speech recognition model profiles.
/// Each profile represents a different size/speed/accuracy tradeoff.
/// </summary>
public enum ModelProfile
{
    // Whisper English-only models (fast, English optimized)
    Tiny,           // tiny.en - Fastest, lowest accuracy
    Base,           // base.en - Fast, better accuracy
    Small,          // small.en - Balanced
    Medium,         // medium.en - Good accuracy, slower

    // Whisper Multilingual models
    Large,          // large-v3 - Best accuracy, slowest, 99+ languages
    LargeTurbo,     // large-v3-turbo - 6x faster than Large, 99+ languages (RECOMMENDED)

    // Distil-Whisper (English-optimized, fastest Whisper variant)
    DistilLargeV3,  // distil-large-v3 - Fastest English, near Large accuracy

    // SenseVoice (Alibaba) - 50+ languages, 15x faster than Whisper-Large
    SenseVoice,

    // Quantized models — smaller download, faster inference, ~same accuracy
    SmallQ5,        // small.en quantized - 190 MB vs 466 MB, ideal for CPU
    LargeTurboQ5,   // large-v3-turbo quantized - 574 MB vs 1.6 GB, ideal for CPU multilingual
    MediumQ5        // medium.en quantized - 539 MB vs 1.5 GB, ideal for CPU
}

/// <summary>
/// The transcription engine type for a model.
/// </summary>
public enum TranscriptionEngine
{
    Whisper,        // Whisper.net (whisper.cpp)
    SherpaOnnx      // sherpa-onnx (SenseVoice, Moonshine)
}

public static class ModelProfileExtensions
{
    /// <summary>
    /// Gets the transcription engine required for this model profile.
    /// </summary>
    public static TranscriptionEngine GetEngine(this ModelProfile profile) => profile switch
    {
        ModelProfile.SenseVoice => TranscriptionEngine.SherpaOnnx,
        _ => TranscriptionEngine.Whisper
    };

    /// <summary>
    /// Gets the model file name for download and storage.
    /// </summary>
    public static string GetModelFileName(this ModelProfile profile) => profile switch
    {
        ModelProfile.Tiny => "ggml-tiny.en.bin",
        ModelProfile.Base => "ggml-base.en.bin",
        ModelProfile.Small => "ggml-small.en.bin",
        ModelProfile.Medium => "ggml-medium.en.bin",
        ModelProfile.Large => "ggml-large-v3.bin",
        ModelProfile.LargeTurbo => "ggml-large-v3-turbo.bin",
        ModelProfile.DistilLargeV3 => "ggml-distil-large-v3.bin",
        ModelProfile.SenseVoice => "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17",
        ModelProfile.SmallQ5 => "ggml-small.en-q5_1.bin",
        ModelProfile.LargeTurboQ5 => "ggml-large-v3-turbo-q5_0.bin",
        ModelProfile.MediumQ5 => "ggml-medium.en-q5_0.bin",
        _ => "ggml-tiny.en.bin"
    };

    /// <summary>
    /// Gets a user-friendly display name with size and key characteristics.
    /// </summary>
    public static string GetDisplayName(this ModelProfile profile) => profile switch
    {
        ModelProfile.Tiny => "Tiny (75 MB) - Fastest",
        ModelProfile.Base => "Base (142 MB) - Fast",
        ModelProfile.Small => "Small (466 MB) - Balanced",
        ModelProfile.Medium => "Medium (1.5 GB) - Accurate",
        ModelProfile.Large => "Large v3 (3.1 GB) - Best, Multilingual",
        ModelProfile.LargeTurbo => "Large v3 Turbo (1.6 GB) - Fast & Multilingual",
        ModelProfile.DistilLargeV3 => "Distil Large v3 (756 MB) - Fastest English",
        ModelProfile.SenseVoice => "SenseVoice (1 GB) - Ultra Fast",
        ModelProfile.SmallQ5 => "Small Lite (190 MB) - Fast English, Low RAM",
        ModelProfile.LargeTurboQ5 => "Large Turbo Lite (574 MB) - Fast Multilingual, Low RAM",
        ModelProfile.MediumQ5 => "Medium Lite (539 MB) - Accurate English, Low RAM",
        _ => "Unknown"
    };

    /// <summary>
    /// Gets a short description of the model's key characteristics.
    /// </summary>
    public static string GetDescription(this ModelProfile profile) => profile switch
    {
        ModelProfile.Tiny => "English only. Best for quick notes.",
        ModelProfile.Base => "English only. Good balance of speed and accuracy.",
        ModelProfile.Small => "English only. Reliable everyday use.",
        ModelProfile.Medium => "English only. High accuracy, slower processing.",
        ModelProfile.Large => "99+ languages. Highest accuracy, slowest.",
        ModelProfile.LargeTurbo => "99+ languages. 6x faster than Large, recommended for multilingual.",
        ModelProfile.DistilLargeV3 => "English only. Near-Large accuracy at 6x speed.",
        ModelProfile.SenseVoice => "50+ languages. 15x faster than Whisper-Large. Alibaba model.",
        ModelProfile.SmallQ5 => "English only. Quantized for speed — ideal for CPU-only systems.",
        ModelProfile.LargeTurboQ5 => "99+ languages. Quantized — best for CPU/iGPU systems.",
        ModelProfile.MediumQ5 => "English only. Quantized for speed — good accuracy on slower hardware.",
        _ => ""
    };

    /// <summary>
    /// Gets the download URL for the model.
    /// </summary>
    public static string GetDownloadUrl(this ModelProfile profile) => profile switch
    {
        // Whisper models from HuggingFace (ggerganov/whisper.cpp)
        ModelProfile.Tiny => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin",
        ModelProfile.Base => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin",
        ModelProfile.Small => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin",
        ModelProfile.Medium => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.en.bin",
        ModelProfile.Large => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin",
        ModelProfile.LargeTurbo => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin",
        ModelProfile.DistilLargeV3 => "https://huggingface.co/distil-whisper/distil-large-v3-ggml/resolve/main/ggml-distil-large-v3.bin",
        // SherpaOnnx models - these are directories, handled differently
        ModelProfile.SenseVoice => "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17.tar.bz2",
        // Quantized models from HuggingFace
        ModelProfile.SmallQ5 => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en-q5_1.bin",
        ModelProfile.LargeTurboQ5 => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin",
        ModelProfile.MediumQ5 => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.en-q5_0.bin",
        _ => ""
    };

    /// <summary>
    /// Gets the expected model file size in bytes (approximate).
    /// </summary>
    public static long GetModelSize(this ModelProfile profile) => profile switch
    {
        ModelProfile.Tiny => 75_000_000,        // ~75 MB
        ModelProfile.Base => 142_000_000,       // ~142 MB
        ModelProfile.Small => 466_000_000,      // ~466 MB
        ModelProfile.Medium => 1_530_000_000,   // ~1.5 GB
        ModelProfile.Large => 3_100_000_000,    // ~3.1 GB
        ModelProfile.LargeTurbo => 1_620_000_000, // ~1.6 GB
        ModelProfile.DistilLargeV3 => 756_000_000, // ~756 MB
        ModelProfile.SenseVoice => 1_050_000_000, // ~1 GB (compressed archive)
        ModelProfile.SmallQ5 => 190_000_000,    // ~190 MB
        ModelProfile.LargeTurboQ5 => 574_000_000, // ~574 MB
        ModelProfile.MediumQ5 => 539_000_000,   // ~539 MB
        _ => 0
    };

    /// <summary>
    /// Gets the model size as a display string.
    /// </summary>
    public static string GetSizeDisplay(this ModelProfile profile) => profile switch
    {
        ModelProfile.Tiny => "75 MB",
        ModelProfile.Base => "142 MB",
        ModelProfile.Small => "466 MB",
        ModelProfile.Medium => "1.5 GB",
        ModelProfile.Large => "3.1 GB",
        ModelProfile.LargeTurbo => "1.6 GB",
        ModelProfile.DistilLargeV3 => "756 MB",
        ModelProfile.SenseVoice => "1 GB",
        ModelProfile.SmallQ5 => "190 MB",
        ModelProfile.LargeTurboQ5 => "574 MB",
        ModelProfile.MediumQ5 => "539 MB",
        _ => "Unknown"
    };

    /// <summary>
    /// Gets supported languages for the model.
    /// </summary>
    public static string[] GetSupportedLanguages(this ModelProfile profile) => profile switch
    {
        ModelProfile.Large => GetWhisperMultilingualLanguages(),
        ModelProfile.LargeTurbo => GetWhisperMultilingualLanguages(),
        ModelProfile.LargeTurboQ5 => GetWhisperMultilingualLanguages(),
        ModelProfile.SenseVoice => ["zh", "en", "ja", "ko", "yue", "auto"],
        _ => ["en"]
    };

    /// <summary>
    /// Checks if this model supports a specific language.
    /// </summary>
    public static bool SupportsLanguage(this ModelProfile profile, string languageCode)
    {
        var supported = profile.GetSupportedLanguages();
        return supported.Contains(languageCode) || supported.Contains("auto");
    }

    /// <summary>
    /// Checks if this model supports automatic language detection.
    /// </summary>
    public static bool SupportsAutoDetect(this ModelProfile profile) => profile switch
    {
        ModelProfile.Large => true,
        ModelProfile.LargeTurbo => true,
        ModelProfile.LargeTurboQ5 => true,
        ModelProfile.SenseVoice => true,
        _ => false
    };

    /// <summary>
    /// Gets the relative speed rating (1-5, higher is faster).
    /// </summary>
    public static int GetSpeedRating(this ModelProfile profile) => profile switch
    {
        ModelProfile.Tiny => 5,
        ModelProfile.Base => 4,
        ModelProfile.Small => 3,
        ModelProfile.Medium => 2,
        ModelProfile.Large => 1,
        ModelProfile.LargeTurbo => 4,
        ModelProfile.DistilLargeV3 => 5,
        ModelProfile.SenseVoice => 5,
        ModelProfile.SmallQ5 => 4,
        ModelProfile.LargeTurboQ5 => 5,
        ModelProfile.MediumQ5 => 3,
        _ => 3
    };

    /// <summary>
    /// Gets the relative accuracy rating (1-5, higher is more accurate).
    /// </summary>
    public static int GetAccuracyRating(this ModelProfile profile) => profile switch
    {
        ModelProfile.Tiny => 2,
        ModelProfile.Base => 3,
        ModelProfile.Small => 3,
        ModelProfile.Medium => 4,
        ModelProfile.Large => 5,
        ModelProfile.LargeTurbo => 4,
        ModelProfile.DistilLargeV3 => 4,
        ModelProfile.SenseVoice => 4,
        ModelProfile.SmallQ5 => 3,
        ModelProfile.LargeTurboQ5 => 4,
        ModelProfile.MediumQ5 => 4,
        _ => 3
    };

    /// <summary>
    /// Checks if this is a recommended/featured model.
    /// </summary>
    public static bool IsRecommended(this ModelProfile profile) => profile switch
    {
        ModelProfile.LargeTurbo => true,      // Best multilingual balance (GPU)
        ModelProfile.DistilLargeV3 => true,   // Best English (GPU)
        ModelProfile.LargeTurboQ5 => true,    // Best multilingual balance (CPU)
        ModelProfile.SmallQ5 => true,         // Best for low-power CPU systems
        _ => false
    };

    // Whisper multilingual language codes (99+ languages)
    private static string[] GetWhisperMultilingualLanguages() =>
    [
        "en", "zh", "de", "es", "ru", "ko", "fr", "ja", "pt", "tr", "pl", "ca",
        "nl", "ar", "sv", "it", "id", "hi", "fi", "vi", "he", "uk", "el", "ms",
        "cs", "ro", "da", "hu", "ta", "no", "th", "ur", "hr", "bg", "lt", "la",
        "mi", "ml", "cy", "sk", "te", "fa", "lv", "bn", "sr", "az", "sl", "kn",
        "et", "mk", "br", "eu", "is", "hy", "ne", "mn", "bs", "kk", "sq", "sw",
        "gl", "mr", "pa", "si", "km", "sn", "yo", "so", "af", "oc", "ka", "be",
        "tg", "sd", "gu", "am", "yi", "lo", "uz", "fo", "ht", "ps", "tk", "nn",
        "mt", "sa", "lb", "my", "bo", "tl", "mg", "as", "tt", "haw", "ln", "ha",
        "ba", "jw", "su", "auto"
    ];

    /// <summary>
    /// SHA256 hash for model file integrity verification (Whisper models only).
    /// Note: These may change on HuggingFace, so we don't strictly enforce them.
    /// </summary>
    public static string? GetExpectedSha256(this ModelProfile profile) => profile switch
    {
        ModelProfile.Tiny => "c78c86eb1a8faa21b369bcd33207cc90d64e9b97ee73eba7e77c6c8a0d9edf1e",
        ModelProfile.Base => "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
        ModelProfile.Small => "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b",
        ModelProfile.Medium => "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208",
        ModelProfile.Large => "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2",
        _ => null
    };
}
