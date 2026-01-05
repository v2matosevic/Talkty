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
    Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples,
        string language = "en",
        CancellationToken cancellationToken = default);

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
