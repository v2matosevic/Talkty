using System.Text.Json;
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
            .GetProperty("enhancedMode").GetProperty("modelOptions").GetProperty("transcribeStyle").GetString();
        Assert.Equal("clean", style);
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

    private static JsonDocument Serialize(ModelProfile profile, string language) =>
        JsonDocument.Parse(JsonSerializer.Serialize(OpenRouterEngine.BuildPayload(profile, "AAAA", language)));
}
