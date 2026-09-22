using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using NAudio.Wave;
using Talkty.App;
using Talkty.App.Models;
using Talkty.App.Services;
using Talkty.App.Services.Engines;
using Whisper.net;
using Whisper.net.LibraryLoader;

// Headless, explicitly supplied fixtures only. Never records, pastes, or reads history.
string? Arg(string key) => args.SkipWhile(a => a != key).Skip(1).FirstOrDefault();
var wav = Arg("--wav") ?? throw new ArgumentException("--wav is required (16 kHz mono PCM16)");
var model = Arg("--model") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Talkty", "Models", "ggml-large-v3-turbo.bin");
var profile = Enum.Parse<ModelProfile>(Arg("--profile") ?? "LargeTurbo");
var rounds = Math.Clamp(int.Parse(Arg("--rounds") ?? "2"), 1, 3);
var stopDelay = Math.Clamp(int.Parse(Arg("--stop-delay-ms") ?? "900"), 0, 2500);
var samples = ReadWave(wav);
var speech = AudioSilenceTrimmer.Trim(samples);
if (profile.IsCloud() && (!args.Contains("--run-paid") || samples.Length > Constants.SampleRate * 40))
    throw new ArgumentException("Cloud requires --run-paid and a fixture no longer than 40 seconds (max 3 rounds / 6 passes, with normal final-pass retries).");
var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Talkty", "settings.json");
var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
// Fixed vocabulary for these public synthetic fixtures, not the user's private terms.
string[] terms = ["Talkty", "OpenRouter", "PostgreSQL", "TypeScript", "C++"];
var output = new List<object>();

if (args.Contains("--cloud-contract-probe"))
{
    if (!profile.IsCloud() || !args.Contains("--run-paid")) throw new ArgumentException("Cloud probe requires --profile and --run-paid");
    var encode = typeof(OpenRouterEngine).GetMethod("EncodeForUpload", BindingFlags.NonPublic | BindingFlags.Static)!;
    var build = typeof(OpenRouterEngine).GetMethod("BuildPayload", BindingFlags.NonPublic | BindingFlags.Static)!;
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKeyProtector.Unprotect(settings.OpenRouterApiKeyEncrypted));
    var variants = (Arg("--variants") ?? "legacy-options,current-options").Split(',');
    if (variants.Length > 3 || variants.Any(v => v is not ("legacy-options" or "current-options" or "no-options")))
        throw new ArgumentException("At most three variants: legacy-options,current-options,no-options");
    foreach (var variant in variants)
    {
        var (audio, format) = ((byte[], string))encode.Invoke(null, [speech, 16000, true, CancellationToken.None])!;
        var payload = (Dictionary<string, object?>)build.Invoke(null, [profile, Convert.ToBase64String(audio), format, "en", terms])!;
        if (variant == "no-options") payload.Remove("provider");
        if (variant == "legacy-options")
        {
            var provider = (Dictionary<string, object?>)payload["provider"]!;
            var options = (Dictionary<string, object?>)provider["options"]!;
            var azure = (Dictionary<string, object?>)options["azure"]!;
            var modelOptions = azure["modelOptions"];
            azure.Remove("modelOptions");
            azure["enhancedMode"] = new Dictionary<string, object?> { ["modelOptions"] = modelOptions };
        }
        using var content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
        var clock = Stopwatch.StartNew();
        using var response = await http.PostAsync("https://openrouter.ai/api/v1/audio/transcriptions", content);
        var body = await response.Content.ReadAsStringAsync();
        var row = new { mode = "contract-probe", variant, status = (int)response.StatusCode, ms = clock.Elapsed.TotalMilliseconds, body };
        output.Add(row);
        Console.WriteLine(JsonSerializer.Serialize(row));
    }
}
else if (args.Contains("--flash-comparison"))
{
    RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda];
    Console.WriteLine($"WhisperFactoryOptions default FlashAttention={new WhisperFactoryOptions().UseFlashAttention}");
    foreach (var flash in new[] { false, true })
    {
        using var factory = WhisperFactory.FromPath(model, new WhisperFactoryOptions { UseGpu = true, UseFlashAttention = flash });
        var greedy = factory.CreateBuilder().WithGreedySamplingStrategy();
        ((GreedySamplingStrategyBuilder)greedy).WithBestOf(1);
        using var processor = greedy.ParentBuilder.WithThreads(Math.Min(8, Math.Max(1, Environment.ProcessorCount / 2)))
            .WithLanguage("en").WithNoContext().WithTemperature(0).WithTemperatureInc(0.2f).Build();
        // Warm the same kernels before timing. No user audio.
        await foreach (var _ in processor.ProcessAsync(new float[8000])) { }
        for (var round = 0; round < rounds; round++)
        {
            var clock = Stopwatch.StartNew();
            var segments = new List<string>();
            await foreach (var segment in processor.ProcessAsync(speech)) segments.Add(segment.Text);
            var row = new { mode = "flash-comparison", flash, round, ms = clock.Elapsed.TotalMilliseconds, text = TextPostProcessor.JoinSegments(segments) };
            output.Add(row);
            Console.WriteLine(JsonSerializer.Serialize(row));
        }
    }
}
else
{
    using var service = new TranscriptionService();
    if (profile.IsCloud()) service.SetCloudApiKey(ApiKeyProtector.Unprotect(settings.OpenRouterApiKeyEncrypted));
    service.SetLanguageHint("en");
    if (!await service.LoadModelAsync(profile, model, !args.Contains("--cpu"))) throw new Exception("Model load failed");
    service.PrewarmCloud();
    await Task.Delay(1000);
    for (var round = 0; round < rounds; round++)
    {
        // Alternate which path goes first to limit warm-connection/GPU ordering bias.
        foreach (var earlyMode in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
        {
            var captured = speech;
            var calls = 0;
            using var early = new SpeculativeTranscriber(() => Volatile.Read(ref captured), n =>
            {
                var snapshot = Volatile.Read(ref captured);
                return snapshot.Length > n ? snapshot[^n..] : snapshot;
            }, async (audio, token) =>
            {
                Interlocked.Increment(ref calls);
                return await service.TranscribeEarlyAsync(audio, "en", token, null, terms);
            }, profile.IsCloud(), default);
            if (earlyMode) early.Start();
            var pause = Stopwatch.StartNew();
            while (pause.ElapsedMilliseconds < stopDelay)
            {
                await Task.Delay(25);
                var silence = new float[(int)(pause.Elapsed.TotalSeconds * Constants.SampleRate)];
                Volatile.Write(ref captured, [.. speech, .. silence]);
            }
            var final = AudioSilenceTrimmer.Trim(captured);
            pause.Stop();
            var clock = Stopwatch.StartNew();
            var result = earlyMode ? await early.CompleteAsync(final, default) : null;
            if (result == null)
            {
                calls++;
                result = await service.TranscribeAsync(final, "en", vocabularyTerms: terms);
            }
            var row = new { mode = earlyMode ? "early" : "baseline", round, profile = profile.ToString(),
                backend = service.BackendInfo, stopDelayMs = pause.Elapsed.TotalMilliseconds,
                stopToResultMs = clock.Elapsed.TotalMilliseconds, reused = early.Reused, calls,
                success = result.Success, text = result.Text, error = result.ErrorMessage };
            output.Add(row);
            Console.WriteLine(JsonSerializer.Serialize(row));
            if (!result.Success)
            {
                if (Arg("--output") is { } failedOutput) File.WriteAllText(failedOutput, JsonSerializer.Serialize(output));
                throw new Exception(result.ErrorMessage);
            }
        }
    }
    if (!profile.IsCloud() && args.Contains("--check-cancel"))
    {
        using var cancellation = new CancellationTokenSource(30);
        try { await service.TranscribeEarlyAsync(speech, "en", cancellation.Token, null, null); }
        catch (OperationCanceledException) { }
        var recovered = await service.TranscribeAsync(speech, "en");
        var row = new { mode = "after-cancellation", success = recovered.Success, text = recovered.Text };
        output.Add(row);
        Console.WriteLine(JsonSerializer.Serialize(row));
        if (!recovered.Success) throw new Exception("Decoder did not recover after cancellation");
    }
}
if (Arg("--output") is { } destination)
    File.WriteAllText(destination, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));

static float[] ReadWave(string path)
{
    using var reader = new WaveFileReader(path);
    if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1 || reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm)
        throw new ArgumentException("Fixture must be mono 16 kHz PCM16 WAV");
    var bytes = new byte[reader.Length];
    reader.ReadExactly(bytes);
    var samples = new float[bytes.Length / 2];
    for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
    return samples;
}
