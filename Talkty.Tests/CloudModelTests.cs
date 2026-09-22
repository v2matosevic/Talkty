using System.Text.Json;
using System.Text;
using System.Diagnostics;
using System.IO;
using Concentus;
using Concentus.Oggfile;
using Talkty.App.Models;
using Talkty.App.Services.Engines;
using Xunit;

namespace Talkty.Tests;

public class CloudModelTests
{
    [Fact]
    public void MaiTranscribe2KeepsItsPersistedNumber()
    {
        // settings.json stores modelProfile by number; a shifted value silently switches users' models.
        Assert.Equal(16, (int)ModelProfile.CloudMaiTranscribe2);
    }

    [Fact]
    public void MaiTranscribe2RoutesToOpenRouter()
    {
        Assert.True(ModelProfile.CloudMaiTranscribe2.IsCloud());
        Assert.Equal("microsoft/mai-transcribe-2", ModelProfile.CloudMaiTranscribe2.GetOpenRouterModelId());
    }

    [Fact]
    public void MaiPayloadAsksForCleanTranscript()
    {
        using var doc = Serialize(ModelProfile.CloudMaiTranscribe2, "en");
        var style = doc.RootElement.GetProperty("provider").GetProperty("options").GetProperty("azure")
            .GetProperty("modelOptions").GetProperty("transcribeStyle").GetString();
        Assert.Equal("clean", style);
        // Live qualification 2026-09-22: nesting under enhancedMode returns 400.
        Assert.False(doc.RootElement.GetProperty("provider").GetProperty("options").GetProperty("azure")
            .TryGetProperty("enhancedMode", out _));
    }

    [Theory]
    [InlineData(ModelProfile.CloudGpt4oTranscribe)]
    [InlineData(ModelProfile.CloudQwen3Asr)]
    public void OtherCloudModelsGetNoAzureOptions(ModelProfile profile)
    {
        using var doc = Serialize(profile, "en");
        Assert.False(doc.RootElement.TryGetProperty("provider", out _));
    }

    [Theory]
    [InlineData("auto", null)]
    [InlineData("", null)]
    [InlineData("en", "en")]
    public void AutoLanguageIsLeftToTheModel(string language, string? expected)
    {
        using var doc = Serialize(ModelProfile.CloudMaiTranscribe2, language);
        var sent = doc.RootElement.TryGetProperty("language", out var el) ? el.GetString() : null;
        Assert.Equal(expected, sent);
    }

    [Fact]
    public void EmptyTranscriptIsDistinctFromMalformedResponse()
    {
        // MAI-Transcribe 2's real answer shape; an empty "text" means no speech, not a broken reply.
        Assert.Equal("", OpenRouterEngine.ExtractText("""{"text":"","usage":{"seconds":4,"cost":0.0001}}"""));
        Assert.Equal("hello", OpenRouterEngine.ExtractText("""{"text":"hello","usage":{"seconds":1,"cost":0.00003}}"""));
        Assert.Null(OpenRouterEngine.ExtractText("""{"error":{"message":"oops"}}"""));
        Assert.Null(OpenRouterEngine.ExtractText("not json"));
    }

    [Fact]
    public void MaiPayloadSendsVocabularyAsPhraseListAlongsideCleanStyle()
    {
        using var doc = Serialize(ModelProfile.CloudMaiTranscribe2, "en", ["Revori", "Kenshi"]);
        var azure = doc.RootElement.GetProperty("provider").GetProperty("options").GetProperty("azure");
        var phrases = azure.GetProperty("phraseList").GetProperty("phrases").EnumerateArray().Select(p => p.GetString()!).ToArray();
        Assert.Equal(["Revori", "Kenshi"], phrases);
        Assert.Equal("clean", azure.GetProperty("modelOptions").GetProperty("transcribeStyle").GetString());
    }

    [Fact]
    public void PhraseListStopsAtFiftyTerms()
    {
        // Azure answers a 51st phrase with a provider 400 (measured 2026-09-15).
        var terms = Enumerable.Range(1, 60).Select(i => $"term{i}").ToList();
        using var doc = Serialize(ModelProfile.CloudMaiTranscribe2, "en", terms);
        var phrases = doc.RootElement.GetProperty("provider").GetProperty("options").GetProperty("azure")
            .GetProperty("phraseList").GetProperty("phrases");
        Assert.Equal(50, phrases.GetArrayLength());
        Assert.Equal("term1", phrases[0].GetString());
    }

    [Fact]
    public void NoPhraseListWithoutVocabulary()
    {
        using var doc = Serialize(ModelProfile.CloudMaiTranscribe2, "en", []);
        var azure = doc.RootElement.GetProperty("provider").GetProperty("options").GetProperty("azure");
        Assert.False(azure.TryGetProperty("phraseList", out _));
    }

    [Fact]
    public void OtherCloudModelsIgnoreVocabularyTerms()
    {
        using var doc = Serialize(ModelProfile.CloudGpt4oMiniTranscribe, "en", ["Revori"]);
        Assert.False(doc.RootElement.TryGetProperty("provider", out _));
    }

    [Fact]
    public void PayloadNamesTheUploadFormat()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            OpenRouterEngine.BuildPayload(ModelProfile.CloudMaiTranscribe2, "AAAA", "mp3", "en")));
        Assert.Equal("mp3", doc.RootElement.GetProperty("input_audio").GetProperty("format").GetString());
    }

    [Fact]
    public void UploadIsCompressedMp3OrFallsBackToWav()
    {
        // One second of a 440 Hz tone. Media Foundation is present on desktop Windows; a machine
        // without it (Windows N, some servers) must still get a valid WAV.
        var samples = Enumerable.Range(0, 16000).Select(i => 0.3f * MathF.Sin(2 * MathF.PI * 440 * i / 16000)).ToArray();
        var (audio, format) = OpenRouterEngine.EncodeForUpload(samples, 16000, preferOpus: false);
        if (format == "mp3")
        {
            Assert.True(audio.Length < samples.Length * 2 / 3, $"MP3 should be far smaller than 16-bit PCM, was {audio.Length} bytes");
        }
        else
        {
            Assert.Equal("wav", format);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(audio, 0, 4));
            Assert.Equal(44 + samples.Length * 2, audio.Length);
        }
    }

    [Fact]
    public void OpusUploadDecodesAndIncludesTheTail()
    {
        // Non-frame-aligned input, with audible data right to the end. Catch forgotten Finish()
        // and mismatched sample rates, which can silently truncate or stretch dictation.
        var samples = Enumerable.Range(0, 16000 * 3 + 137)
            .Select(i => 0.3f * MathF.Sin(2 * MathF.PI * 440 * i / 16000)).ToArray();
        var (audio, format) = OpenRouterEngine.EncodeForUpload(samples, 16000);
        Assert.Equal("opus", format);
        Assert.Equal("OggS", Encoding.ASCII.GetString(audio, 0, 4));
        Assert.True(audio.Length < samples.Length, $"Expected compressed speech upload, got {audio.Length} bytes");
        using var decoder = OpusCodecFactory.CreateDecoder(16000, 1);
        using var stream = new MemoryStream(audio);
        var reader = new OpusOggReadStream(decoder, stream);
        var decoded = new List<short>();
        while (reader.HasNextPacket)
        {
            var packet = reader.DecodeNextPacket();
            if (packet != null) decoded.AddRange(packet);
        }
        Assert.InRange(decoded.Count, samples.Length - 320, samples.Length + 640);
        Assert.Contains(decoded.Skip(samples.Length - 1000), s => Math.Abs((int)s) > 1000);
    }

    [Fact]
    public void CancelledOpusDoesNotFallBackAndKeepEncoding()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            OpenRouterEngine.EncodeForUpload(new float[16000], 16000, cancellationToken: cts.Token));
    }

    [Fact]
    public void CompactJsonPreservesAudioAndVocabularyExactly()
    {
        byte[] audio = [251, 239, 190, 255, 255, 255];
        string[] terms = ["C++", "a \"quoted\" term", "Žuti"];
        var body = OpenRouterEngine.SerializePayload(ModelProfile.CloudMaiTranscribe2, audio, "opus", "auto", terms);
        Assert.DoesNotContain("\\u002B", Encoding.UTF8.GetString(body));
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(audio, doc.RootElement.GetProperty("input_audio").GetProperty("data").GetBytesFromBase64());
        var azure = doc.RootElement.GetProperty("provider").GetProperty("options").GetProperty("azure");
        Assert.Equal(terms, azure.GetProperty("phraseList").GetProperty("phrases").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("clean", azure.GetProperty("modelOptions").GetProperty("transcribeStyle").GetString());
    }

    [Fact]
    public async Task TimedContentSendsExactBytesAndLength()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"text\":\"Žuti\"}");
        using var content = new CloudRequestContent(body, Stopwatch.StartNew());
        Assert.Equal(body.Length, content.Headers.ContentLength);
        Assert.Null(content.BodyWrittenMs);
        using var target = new MemoryStream();
        await content.CopyToAsync(target);
        Assert.Equal(body, target.ToArray());
        Assert.NotNull(content.BodyWrittenMs);
    }

    private static JsonDocument Serialize(ModelProfile profile, string language, IReadOnlyList<string>? terms = null) =>
        JsonDocument.Parse(JsonSerializer.Serialize(OpenRouterEngine.BuildPayload(profile, "AAAA", "wav", language, terms)));
}
