using Talkty.App.Models;

namespace Talkty.App.Services;

/// <summary>
/// Options for transcription operations.
/// </summary>
public record TranscriptionOptions
{
    /// <summary>
    /// Language code for transcription (e.g., "en", "es", "auto").
    /// Use "auto" for automatic language detection on supported models.
    /// </summary>
    public string Language { get; init; } = "en";

    /// <summary>
    /// Whether to translate the transcription to English.
    /// Only supported by some models (Whisper multilingual).
    /// </summary>
    public bool TranslateToEnglish { get; init; } = false;

    /// <summary>
    /// Timeout for transcription operation in milliseconds.
    /// </summary>
    public int TimeoutMs { get; init; } = 30000;
}

/// <summary>
/// Abstraction for speech-to-text transcription engines.
/// Allows multiple engine implementations (Whisper, SenseVoice, Parakeet, etc.).
/// </summary>
public interface ITranscriptionEngine : IDisposable
{
    /// <summary>
    /// Human-readable name of the engine (e.g., "Whisper", "SenseVoice").
    /// </summary>
    string EngineName { get; }

    /// <summary>
    /// The type of engine.
    /// </summary>
    TranscriptionEngine EngineType { get; }

    /// <summary>
    /// Whether a model is currently loaded and ready for transcription.
    /// </summary>
    bool IsModelLoaded { get; }

    /// <summary>
    /// The currently loaded model profile, or null if no model is loaded.
    /// </summary>
    ModelProfile? CurrentProfile { get; }

    /// <summary>
    /// Backend information string (e.g., "CPU (whisper.cpp)", "CUDA").
    /// </summary>
    string? BackendInfo { get; }

    /// <summary>
    /// Languages supported by the currently loaded model.
    /// </summary>
    IReadOnlyList<string> SupportedLanguages { get; }

    /// <summary>
    /// Loads a model from the specified path.
    /// </summary>
    /// <param name="profile">The model profile to load.</param>
    /// <param name="modelPath">Path to the model file or directory.</param>
    /// <param name="useGpu">Whether to use GPU acceleration if available.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the model was loaded successfully.</returns>
    Task<bool> LoadModelAsync(ModelProfile profile, string modelPath, bool useGpu = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Transcribes audio samples to text.
    /// </summary>
    /// <param name="audioSamples">Audio samples as 16-bit PCM at 16kHz mono.</param>
    /// <param name="options">Transcription options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Transcription result with text and metadata.</returns>
    Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples,
        TranscriptionOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if this engine can handle the specified model profile.
    /// </summary>
    bool CanHandleProfile(ModelProfile profile);
}
