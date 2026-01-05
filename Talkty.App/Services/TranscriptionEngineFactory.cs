using Talkty.App.Models;
using Talkty.App.Services.Engines;

namespace Talkty.App.Services;

/// <summary>
/// Factory for creating transcription engine instances based on model profile.
/// </summary>
public static class TranscriptionEngineFactory
{
    /// <summary>
    /// Creates the appropriate transcription engine for the given model profile.
    /// </summary>
    public static ITranscriptionEngine CreateEngine(ModelProfile profile)
    {
        var engineType = profile.GetEngine();
        return CreateEngine(engineType);
    }

    /// <summary>
    /// Creates a transcription engine of the specified type.
    /// </summary>
    public static ITranscriptionEngine CreateEngine(TranscriptionEngine engineType)
    {
        return engineType switch
        {
            TranscriptionEngine.Whisper => new WhisperEngine(),
            TranscriptionEngine.SherpaOnnx => new SherpaOnnxEngine(),
            _ => throw new ArgumentException($"Unknown engine type: {engineType}")
        };
    }

    /// <summary>
    /// Gets all available engine types.
    /// </summary>
    public static IReadOnlyList<TranscriptionEngine> GetAvailableEngines() =>
        [TranscriptionEngine.Whisper, TranscriptionEngine.SherpaOnnx];

    /// <summary>
    /// Gets all model profiles for a given engine type.
    /// </summary>
    public static IReadOnlyList<ModelProfile> GetProfilesForEngine(TranscriptionEngine engineType) =>
        Enum.GetValues<ModelProfile>()
            .Where(p => p.GetEngine() == engineType)
            .ToArray();
}
