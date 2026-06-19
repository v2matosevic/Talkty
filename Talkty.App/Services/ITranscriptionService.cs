using Talkty.App.Models;

namespace Talkty.App.Services;

public interface ITranscriptionService : IDisposable
{
    /// <summary>
    /// Loads a model for transcription.
    /// </summary>
    /// <param name="profile">The model profile to load.</param>
    /// <param name="modelPath">Path to the model file.</param>
    /// <param name="useGpu">Whether to use GPU acceleration if available.</param>
    Task<bool> LoadModelAsync(ModelProfile profile, string modelPath, bool useGpu = false);

    /// <summary>
    /// Transcribes audio samples to text using the loaded model.
    /// </summary>
    /// <param name="audioSamples">Audio samples as 16-bit PCM at 16kHz mono.</param>
    /// <param name="language">Language code (e.g., "en", "auto"). Defaults to "en".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="onFirstSegment">Optional callback fired when the first segment is ready (for early clipboard copy).</param>
    Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples,
        string language = "en",
        CancellationToken cancellationToken = default,
        Action<string>? onFirstSegment = null,
        string? vocabularyPrompt = null);

    /// <summary>
    /// Pre-sets the vocabulary prompt so the processor is built with it on model load.
    /// Call before LoadModelAsync to avoid a rebuild on first transcription.
    /// </summary>
    void SetVocabularyPrompt(string? prompt);

    /// <summary>
    /// Sets the (decrypted) OpenRouter API key used by the cloud engine. Forwarded to the
    /// engine on creation, mirroring <see cref="SetVocabularyPrompt"/>. Pass null to clear.
    /// </summary>
    void SetCloudApiKey(string? apiKey);

    /// <summary>
    /// Whether a model is currently loaded and ready.
    /// </summary>
    bool IsModelLoaded { get; }

    /// <summary>
    /// The currently loaded model profile.
    /// </summary>
    ModelProfile? CurrentProfile { get; }

    /// <summary>
    /// Backend information string.
    /// </summary>
    string? BackendInfo { get; }

    /// <summary>
    /// Languages supported by the currently loaded model.
    /// </summary>
    IReadOnlyList<string> SupportedLanguages { get; }

    /// <summary>
    /// Default timeout for transcription operations.
    /// </summary>
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
}
