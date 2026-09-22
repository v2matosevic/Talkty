using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using NAudio.MediaFoundation;
using NAudio.Wave;
using Talkty.App.Models;

namespace Talkty.App.Services.Engines;

/// <summary>
/// Cloud transcription engine backed by OpenRouter's audio API.
/// Sends recorded audio (Opus for MAI, MP3/WAV fallback) to <c>POST /api/v1/audio/transcriptions</c> and returns
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

    // JSON is sent only to the API, never embedded in HTML. Avoid escaping '+' in base64 audio.
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly Lazy<Task> EncoderWarmup = new(() => Task.Run(() =>
    {
        EncodeForUpload(new float[Constants.SampleRate / 10], Constants.SampleRate);
        TryEncodeMp3(new float[Constants.SampleRate / 10], Constants.SampleRate);
    }));
    private static readonly Lazy<bool> NativeOpusAvailable = new(() =>
    {
        // Load only our packaged library. The codec's default search uses the host executable's
        // directory, which is wrong when hosted by tests/PowerShell. Keep it loaded for process life.
        var root = Path.GetDirectoryName(typeof(OpenRouterEngine).Assembly.Location)!;
        foreach (var path in new[] { Path.Combine(root, "opus.dll"), Path.Combine(root, "runtimes", "win-x64", "native", "opus.dll") })
        {
            if (NativeLibrary.TryLoad(path, out _)) return true;
        }
        return false;
    });

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
    private readonly HttpClient _http;
    private readonly bool _skipPrewarm;
    public OpenRouterEngine() : this(Http, false) { }
    internal OpenRouterEngine(HttpClient http, bool skipPrewarm = true)
    {
        _http = http;
        _skipPrewarm = skipPrewarm;
    }
    private string? _apiKey;
    private Task? _prewarmTask;

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
    public Task PrewarmAsync()
    {
        if (_skipPrewarm) return Task.CompletedTask;
        lock (_lock)
        {
            // Callers awaiting warm-up must await the active work, not an already-completed task.
            return _prewarmTask is { IsCompleted: false } ? _prewarmTask : _prewarmTask = RunPrewarmAsync();
        }
    }

    private async Task RunPrewarmAsync()
    {
        try
        {
            // Start both independently. Encoder initialization must not postpone the TLS handshake.
            var encoderWarmup = EncoderWarmup.Value;
            var key = _apiKey;
            if (string.IsNullOrWhiteSpace(key)) return;
            using var request = new HttpRequestMessage(HttpMethod.Get, KeyEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var cts = new CancellationTokenSource(5000);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token);
            Log.Debug($"Cloud prewarm done: HTTP {(int)response.StatusCode}");
            await encoderWarmup;
        }
        catch (Exception ex)
        {
            Log.Debug($"Cloud prewarm skipped: {ex.Message}");
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
        // Cover very short first dictations too; this never delays model readiness.
        _ = PrewarmAsync();
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
        var clock = Stopwatch.StartNew();

        // Cloud needs a longer budget than the local 30s default (network + queue + inference).
        // ESC still aborts immediately via the caller's cancellationToken.
        using var timeoutCts = new CancellationTokenSource(Constants.CloudTranscriptionTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            // Off the UI thread: Media Foundation wants an MTA thread, and a long take encodes in ~0.3 s.
            // MAI preserves technical terms with 24 kbps Opus. Qwen Flash lost "C++"
            // on the same fixture with Opus (2026-09-18); keep its 48 kbps MP3 path.
            var preferOpus = profile == ModelProfile.CloudMaiTranscribe2;
            (byte[] Audio, string Format) prepared;
            if (options.CloudAudioCache == null || !options.CloudAudioCache.TryGetValue(preferOpus, out prepared))
            {
                prepared = await Task.Run(() => EncodeForUpload(audioSamples, Constants.SampleRate,
                    preferOpus, linkedCts.Token), linkedCts.Token);
                if (options.CloudAudioCache != null) options.CloudAudioCache[preferOpus] = prepared;
            }
            var (audio, format) = prepared;
            var encodeMs = clock.ElapsedMilliseconds;
            var hints = profile == ModelProfile.CloudMaiTranscribe2
                ? Math.Min(options.VocabularyTerms?.Count ?? 0, Constants.CloudMaxVocabularyTerms)
                : 0;
            Log.Info($"Upload: {audio.Length:N0} bytes {format}, {hints} vocabulary hints");
            var json = SerializePayload(profile.Value, audio, format, options.Language, options.VocabularyTerms);
            var payloadMs = clock.ElapsedMilliseconds - encodeMs;
            Log.Info($"Cloud preparation: encode={encodeMs}ms, payload={payloadMs}ms, body={json.Length} bytes");

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
                var requestClock = Stopwatch.StartNew();
                var content = new CloudRequestContent(json, requestClock);
                request.Content = content;

                Log.Debug($"POST {Endpoint} ({audio.Length} bytes {format}, {json.Length} bytes body, attempt {attempt})");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
                var headersMs = requestClock.ElapsedMilliseconds;
                body = await response.Content.ReadAsStringAsync(linkedCts.Token);
                status = response.StatusCode;
                Log.Info($"Cloud HTTP: attempt={attempt}, status={(int)status}, version={response.Version}, " +
                    $"bodyWritten={content.BodyWrittenMs?.ToString() ?? "unknown"}ms, headers={headersMs}ms, " +
                    $"responseRead={requestClock.ElapsedMilliseconds - headersMs}ms, total={requestClock.ElapsedMilliseconds}ms");

                if (response.IsSuccessStatusCode) break;

                bool transient = (int)status is 408 or 429 || (int)status >= 500;
                if (transient && attempt == 1 && options.RetryTransientCloudErrors)
                {
                    Log.Warning($"OpenRouter HTTP {(int)status} — transient, retrying once after 1s");
                    var retryAfter = response.Headers.RetryAfter;
                    var delay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(1);
                    // A long provider cooldown is better handled by the independent fallback.
                    if (delay > TimeSpan.FromSeconds(5))
                        return Fail(DescribeHttpError(status, body), startTime, true);
                    await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), linkedCts.Token);
                    continue;
                }

                return Fail(DescribeHttpError(status, body), startTime, transient);
            }

            var text = ExtractText(body);
            var elapsed = clock.Elapsed;

            if (text is null)
            {
                return Fail("Cloud transcription returned no text.", startTime, true);
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
            return Fail($"Cloud transcription timed out after {Constants.CloudTranscriptionTimeoutMs / 1000}s.", startTime, true);
        }
        catch (OperationCanceledException)
        {
            Log.Info("Cloud transcription cancelled");
            return Fail("Transcription was cancelled", startTime);
        }
        catch (Exception ex)
        {
            Log.Error("OpenRouterEngine.TranscribeAsync exception", ex);
            return Fail($"Cloud transcription failed: {ex.Message}", startTime, ex is HttpRequestException or IOException);
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
                // modelOptions is a sibling of phraseList, not a child of
                // enhancedMode. The nested form returns provider HTTP 400.
                ["modelOptions"] = new Dictionary<string, object?> { ["transcribeStyle"] = "clean" }
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

    internal static byte[] SerializePayload(ModelProfile profile, byte[] audio, string format,
        string? language, IReadOnlyList<string>? vocabularyTerms = null) =>
        JsonSerializer.SerializeToUtf8Bytes(BuildPayload(profile, Convert.ToBase64String(audio), format,
            language, vocabularyTerms), PayloadJsonOptions);

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

    private static TranscriptionResult Fail(string message, DateTime? startTime = null, bool canUseFallback = false) => new()
    {
        CanUseFallback = canUseFallback,
        Success = false,
        ErrorMessage = message,
        Timestamp = startTime ?? DateTime.Now,
        Duration = startTime.HasValue ? DateTime.Now - startTime.Value : TimeSpan.Zero
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Native Opus for MAI, MP3 for other providers or when Opus cannot initialize, then WAV.
    /// Native encoding keeps CPU preparation comparable to MP3 with less than half the upload.
    /// </summary>
    internal static (byte[] Audio, string Format) EncodeForUpload(float[] samples, int sampleRate,
        bool preferOpus = true, CancellationToken cancellationToken = default)
    {
        if (preferOpus)
        {
            try { return (EncodeOpus(samples, sampleRate, cancellationToken), "opus"); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warning($"Opus encoding unavailable; trying MP3: {ex.Message}"); }
        }
        var mp3 = TryEncodeMp3(samples, sampleRate);
        return mp3 != null ? (mp3, "mp3") : (EncodeWav(samples, sampleRate), "wav");
    }

    internal static byte[] EncodeOpus(float[] samples, int sampleRate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!NativeOpusAvailable.Value)
            throw new NotSupportedException("Packaged native Opus encoder is missing; managed encoding would delay transcription.");
        using var encoder = OpusCodecFactory.CreateEncoder(sampleRate, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
        if (encoder is Concentus.Structs.OpusEncoder)
            throw new NotSupportedException("Native Opus was not selected; using the faster MP3 fallback.");
        Log.Debug($"Cloud Opus encoder: {encoder.GetType().Name}, {encoder.GetVersionString()}");
        encoder.Bitrate = Constants.CloudOpusBitRate;
        encoder.Complexity = 5;
        using var output = new MemoryStream();
        var writer = new OpusOggWriteStream(encoder, output, inputSampleRate: sampleRate, leaveOpen: true);
        // Bound cancellation latency and avoid making another full PCM copy for long recordings.
        for (int offset = 0; offset < samples.Length; offset += sampleRate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteSamples(samples, offset, Math.Min(sampleRate, samples.Length - offset));
        }
        writer.Finish();
        return output.ToArray();
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
