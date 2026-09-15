using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NAudio.MediaFoundation;
using NAudio.Wave;
using Talkty.App.Models;

namespace Talkty.App.Services.Engines;

/// <summary>
/// Cloud transcription engine backed by OpenRouter's audio API.
/// Sends recorded audio (MP3, WAV fallback) to <c>POST /api/v1/audio/transcriptions</c> and returns
/// the transcribed text. The model is selected per <see cref="ModelProfile"/> (GPT-4o
/// Transcribe, Whisper Large V3, Qwen3 ASR, etc.).
///
/// Unlike the local engines this is NOT offline and incurs per-use cost. It exists as an
/// opt-in "high quality" backend — the local Whisper path remains the privacy-first default.
/// </summary>
public class OpenRouterEngine : ITranscriptionEngine
{
    private const string Endpoint = "https://openrouter.ai/api/v1/audio/transcriptions";

    private const string KeyEndpoint = "https://openrouter.ai/api/v1/key";

    // One HttpClient for the engine's lifetime — creating per-request exhausts sockets.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        // Dictations are often minutes apart; the default pool drops the TLS connection after a
        // minute, so the next request paid a fresh handshake. PrewarmAsync reopens it regardless.
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10)
    })
    {
        // Hard ceiling; the per-request timeout is enforced via the linked CancellationToken.
        Timeout = TimeSpan.FromMilliseconds(Constants.CloudTranscriptionTimeoutMs + 10_000)
    };

    // Set once Media Foundation fails (Windows N editions ship without it): WAV from then on.
    private static volatile bool _mp3Unavailable;

    private readonly object _lock = new();
    private string? _apiKey;
    private int _prewarming;

    public string EngineName => "OpenRouter";
    public TranscriptionEngine EngineType => TranscriptionEngine.OpenRouter;
    public ModelProfile? CurrentProfile { get; private set; }
    public string? BackendInfo { get; private set; }

    // The model is remote — "loaded" means we have a selected cloud profile AND an API key.
    public bool IsModelLoaded => CurrentProfile != null && !string.IsNullOrWhiteSpace(_apiKey);

    public IReadOnlyList<string> SupportedLanguages =>
        CurrentProfile?.GetSupportedLanguages() ?? ["en"];

    public bool CanHandleProfile(ModelProfile profile) =>
        profile.GetEngine() == TranscriptionEngine.OpenRouter;

    /// <summary>
    /// Sets the OpenRouter API key. Forwarded by <see cref="TranscriptionService"/> the same
    /// way the vocabulary prompt is handed to <see cref="WhisperEngine"/>.
    /// </summary>
    public void SetApiKey(string? apiKey)
    {
        lock (_lock)
        {
            _apiKey = apiKey?.Trim();
        }
    }

    /// <summary>
    /// Runs at recording start: starts Media Foundation and opens the HTTPS connection while the
    /// user is still speaking. Measured 2026-09-15: the first request in a fresh process took
    /// 2.6 s against 1.1 s warm, and the first MP3 encode ~0.5 s of encoder startup. Never throws.
    /// </summary>
    public async Task PrewarmAsync()
    {
        if (Interlocked.Exchange(ref _prewarming, 1) == 1) return;
        try
        {
            await Task.Run(() => TryEncodeMp3(new float[Constants.SampleRate / 10], Constants.SampleRate));

            var key = _apiKey;
            if (string.IsNullOrWhiteSpace(key)) return;
            using var request = new HttpRequestMessage(HttpMethod.Get, KeyEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var cts = new CancellationTokenSource(5000);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Log.Debug($"Cloud prewarm done: HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            Log.Debug($"Cloud prewarm skipped: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _prewarming, 0);
        }
    }

    public Task<bool> LoadModelAsync(
        ModelProfile profile,
        string modelPath,
        bool useGpu = false,
        CancellationToken cancellationToken = default)
    {
        if (!CanHandleProfile(profile))
        {
            Log.Error($"OpenRouterEngine cannot handle profile: {profile}");
            return Task.FromResult(false);
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            Log.Warning("OpenRouterEngine.LoadModelAsync: no API key configured");
            BackendInfo = "OpenRouter (no API key)";
            CurrentProfile = null;
            return Task.FromResult(false);
        }

        // Nothing to download or initialize — the model lives on OpenRouter. We just record
        // the selection so IsModelLoaded flips true and transcription can proceed.
        CurrentProfile = profile;
        BackendInfo = $"OpenRouter (cloud) — {profile.GetOpenRouterModelId()}";
        Log.Info($"OpenRouterEngine ready: model={profile.GetOpenRouterModelId()}");
        return Task.FromResult(true);
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples,
        TranscriptionOptions options,
        CancellationToken cancellationToken = default)
    {
        var profile = CurrentProfile;
        var modelId = profile?.GetOpenRouterModelId();
        var audioDuration = audioSamples.Length / (double)Constants.SampleRate;
        Log.Section("CLOUD TRANSCRIPTION (OpenRouter)");
        Log.Info($"Model: {modelId}");
        Log.Info($"Audio: {audioSamples.Length} samples ({audioDuration:F1}s)");
        Log.Info($"Language: {options.Language}");

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return Fail("OpenRouter API key not set — add it in Settings.");
        }
        if (profile is null || string.IsNullOrWhiteSpace(modelId))
        {
            return Fail("No cloud model selected.");
        }

        // OpenRouter gives the upstream provider ~60s to answer, which long files can exceed on
        // slower models — chunking is a future enhancement. MAI-Transcribe 2 is exempt: it returned
        // a 144s clip in ~10s (measured 2026-09-15).
        if (audioDuration > Constants.CloudMaxAudioSeconds && profile != ModelProfile.CloudMaiTranscribe2)
        {
            Log.Warning($"Audio is {audioDuration:F0}s — exceeds OpenRouter's ~{Constants.CloudMaxAudioSeconds}s/request limit; the call may time out. Consider a shorter recording.");
        }

        var startTime = DateTime.Now;

        // Cloud needs a longer budget than the local 30s default (network + queue + inference).
        // ESC still aborts immediately via the caller's cancellationToken.
        using var timeoutCts = new CancellationTokenSource(Constants.CloudTranscriptionTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            // Off the UI thread: Media Foundation wants an MTA thread, and a long take encodes in ~0.3 s.
            var (audio, format) = await Task.Run(() => EncodeForUpload(audioSamples, Constants.SampleRate), linkedCts.Token);
            var hints = profile == ModelProfile.CloudMaiTranscribe2
                ? Math.Min(options.VocabularyTerms?.Count ?? 0, Constants.CloudMaxVocabularyTerms)
                : 0;
            Log.Info($"Upload: {audio.Length:N0} bytes {format}, {hints} vocabulary hints");
            var json = JsonSerializer.Serialize(BuildPayload(profile.Value, Convert.ToBase64String(audio), format, options.Language, options.VocabularyTerms));

            // One retry on transient failures (rate limit / gateway hiccups). Auth and
            // client errors are permanent — retrying those just doubles the wait.
            string body;
            System.Net.HttpStatusCode status;
            int attempt = 0;
            while (true)
            {
                attempt++;
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                Log.Debug($"POST {Endpoint} ({audio.Length} bytes {format}, {json.Length} bytes body, attempt {attempt})");
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, linkedCts.Token);
                body = await response.Content.ReadAsStringAsync(linkedCts.Token);
                status = response.StatusCode;

                if (response.IsSuccessStatusCode) break;

                bool transient = (int)status is 429 or 500 or 502 or 503;
                if (transient && attempt == 1)
                {
                    Log.Warning($"OpenRouter HTTP {(int)status} — transient, retrying once after 1s");
                    await Task.Delay(1000, linkedCts.Token);
                    continue;
                }

                return Fail(DescribeHttpError(status, body), startTime);
            }

            var text = ExtractText(body);
            var elapsed = DateTime.Now - startTime;

            if (text is null)
            {
                return Fail("Cloud transcription returned no text.", startTime);
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                // A well-formed empty transcript means the model heard no speech (MAI-Transcribe 2
                // answers a quiet, speechless mic this way). Same wording as the local path.
                Log.Info("Cloud transcription heard no speech");
                return Fail("No speech was detected. Nothing was copied.", startTime);
            }

            // NOTE: deliberately do NOT fire OnFirstSegment here. That callback is the
            // local-streaming optimization (copy partial text early). Cloud is non-streaming —
            // the full transcript arrives at once, so firing it would write the clipboard twice
            // (first-segment + full text) within ~2ms. The second write collides with the clipboard
            // manager that the first write wakes up, blocking ~3s on Windows' OLE retry. One write only.

            Log.Info($"Cloud transcription completed in {elapsed.TotalMilliseconds:F0}ms, {text.Length} chars");
            return new TranscriptionResult
            {
                Text = text.Trim(),
                Timestamp = startTime,
                Duration = elapsed,
                Success = true
            };
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            Log.Error($"Cloud transcription timed out after {Constants.CloudTranscriptionTimeoutMs / 1000}s");
            return Fail($"Cloud transcription timed out after {Constants.CloudTranscriptionTimeoutMs / 1000}s.", startTime);
        }
        catch (OperationCanceledException)
        {
            Log.Info("Cloud transcription cancelled");
            return Fail("Transcription was cancelled", startTime);
        }
        catch (Exception ex)
        {
            Log.Error("OpenRouterEngine.TranscribeAsync exception", ex);
            return Fail($"Cloud transcription failed: {ex.Message}", startTime);
        }
    }

    /// <summary>
    /// Request body for <c>/audio/transcriptions</c>. Internal for tests.
    /// </summary>
    internal static Dictionary<string, object?> BuildPayload(
        ModelProfile profile, string base64Audio, string format, string? language, IReadOnlyList<string>? vocabularyTerms = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = profile.GetOpenRouterModelId(),
            ["input_audio"] = new Dictionary<string, object?>
            {
                ["data"] = base64Audio,
                ["format"] = format
            },
            // Deterministic output to match the local engines' zero-temperature behaviour.
            ["temperature"] = 0
        };

        // language="auto" (or empty) → omit the field so the model auto-detects.
        if (!string.IsNullOrWhiteSpace(language) && language != "auto")
            payload["language"] = language;

        // OpenRouter forwards provider.options.azure to Azure untouched (an invalid value or a 51st
        // phrase comes back as a provider 400), so these options go only to the model that owns them.
        if (profile == ModelProfile.CloudMaiTranscribe2)
        {
            var azure = new Dictionary<string, object?>
            {
                // Default "verbatim" keeps "um", "uh" and false starts in the pasted text; "clean"
                // matches what Whisper gives.
                ["enhancedMode"] = new Dictionary<string, object?>
                {
                    ["modelOptions"] = new Dictionary<string, object?> { ["transcribeStyle"] = "clean" }
                }
            };

            // Keyword biasing from the user's saved vocabulary (additions first).
            if (vocabularyTerms is { Count: > 0 })
            {
                azure["phraseList"] = new Dictionary<string, object?>
                {
                    ["phrases"] = vocabularyTerms.Take(Constants.CloudMaxVocabularyTerms).ToArray()
                };
            }

            payload["provider"] = new Dictionary<string, object?>
            {
                ["options"] = new Dictionary<string, object?> { ["azure"] = azure }
            };
        }

        return payload;
    }

    /// <summary>
    /// Pulls the transcript out of OpenRouter's response: <c>{ "text": "...", "usage": {...} }</c>.
    /// Null means the response was malformed; an empty string means no speech was recognized.
    /// </summary>
    internal static string? ExtractText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
            {
                if (doc.RootElement.TryGetProperty("usage", out var usage) &&
                    usage.TryGetProperty("cost", out var cost))
                {
                    Log.Info($"Cloud request cost: ${cost.GetDouble():F6}");
                }
                return textEl.GetString();
            }
            Log.Warning($"Response had no 'text' field: {Truncate(body, 500)}");
            return null;
        }
        catch (JsonException ex)
        {
            Log.Error($"Failed to parse OpenRouter response: {ex.Message}. Body: {Truncate(body, 500)}");
            return null;
        }
    }

    private static string DescribeHttpError(System.Net.HttpStatusCode status, string body)
    {
        var detail = Truncate(body, 300);
        Log.Error($"OpenRouter HTTP {(int)status} {status}: {detail}");
        return status switch
        {
            System.Net.HttpStatusCode.Unauthorized => "Invalid OpenRouter API key.",
            System.Net.HttpStatusCode.PaymentRequired => "OpenRouter credits exhausted — top up your account.",
            System.Net.HttpStatusCode.TooManyRequests => "OpenRouter rate limit hit — try again shortly.",
            _ => $"Cloud error {(int)status}: {status}"
        };
    }

    private static TranscriptionResult Fail(string message, DateTime? startTime = null) => new()
    {
        Success = false,
        ErrorMessage = message,
        Timestamp = startTime ?? DateTime.Now,
        Duration = startTime.HasValue ? DateTime.Now - startTime.Value : TimeSpan.Zero
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// MP3 when Windows' Media Foundation encoder is available, otherwise WAV. Upload time dominated
    /// cloud latency: a 144 s take uploaded in 26 s as WAV and 5 s as MP3, with the same words.
    /// </summary>
    internal static (byte[] Audio, string Format) EncodeForUpload(float[] samples, int sampleRate)
    {
        var mp3 = TryEncodeMp3(samples, sampleRate);
        return mp3 != null ? (mp3, "mp3") : (EncodeWav(samples, sampleRate), "wav");
    }

    private static byte[]? TryEncodeMp3(float[] samples, int sampleRate)
    {
        if (_mp3Unavailable) return null;
        try
        {
            MediaFoundationApi.Startup();
            using var pcm = new RawSourceWaveStream(new MemoryStream(ToPcm16(samples)), new WaveFormat(sampleRate, 16, 1));
            using var output = new MemoryStream();
            MediaFoundationEncoder.EncodeToMp3(pcm, output, Constants.CloudMp3BitRate);
            return output.Length > 0 ? output.ToArray() : null;
        }
        catch (Exception ex)
        {
            _mp3Unavailable = true;
            Log.Warning($"MP3 encoding unavailable, cloud uploads fall back to WAV: {ex.Message}");
            return null;
        }
    }

    private static byte[] ToPcm16(float[] samples)
    {
        var pcm = new byte[samples.Length * sizeof(short)];
        for (int i = 0; i < samples.Length; i++)
        {
            var value = (short)(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            pcm[2 * i] = (byte)value;
            pcm[2 * i + 1] = (byte)(value >> 8);
        }
        return pcm;
    }

    /// <summary>
    /// Encodes float PCM samples (range -1..1) into a 16-bit mono WAV byte array.
    /// The local pipeline keeps audio as float; the cloud API wants an encoded container.
    /// </summary>
    private static byte[] EncodeWav(float[] samples, int sampleRate)
    {
        const int bitsPerSample = 16;
        const int channels = 1;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int dataSize = samples.Length * sizeof(short);

        using var ms = new MemoryStream(44 + dataSize);
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        // RIFF header
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataSize);
        w.Write("WAVE"u8.ToArray());

        // fmt chunk
        w.Write("fmt "u8.ToArray());
        w.Write(16);                         // PCM fmt chunk size
        w.Write((short)1);                   // audio format = PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)(channels * bitsPerSample / 8)); // block align
        w.Write((short)bitsPerSample);

        // data chunk
        w.Write("data"u8.ToArray());
        w.Write(dataSize);
        w.Write(ToPcm16(samples));

        w.Flush();
        return ms.ToArray();
    }

    public void Dispose()
    {
        // The static HttpClient is shared across instances and intentionally not disposed here.
        CurrentProfile = null;
        GC.SuppressFinalize(this);
    }
}
