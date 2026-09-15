using System.Text;
using Talkty.App.Models;
using Talkty.App.Services;
using Xunit;

namespace Talkty.Tests;

public class VocabularyPromptTests
{
    [Fact]
    public void AppendedUserNamesTakePriorityOverThePresetCatalog()
    {
        var settings = new AppSettings
        {
            CustomVocabulary = [.. DefaultVocabulary.CodingTerms, "Northstar", "Zephyr Studio", "QuillForge"]
        };
        var prompt = VocabularyPromptBuilder.Build(settings)!;
        Assert.StartsWith("Northstar, Zephyr Studio, QuillForge, ", prompt);
        Assert.Contains("kubectl", prompt);
        Assert.True(Encoding.UTF8.GetByteCount(prompt) <= 200);
    }

    [Fact]
    public void RemovedWordsAreNotReintroducedAndSpellingIsPreserved()
    {
        var settings = new AppSettings { CustomVocabulary = ["  QuillForge  ", "quillforge", "Zephyr\tStudio", "", "  "] };
        Assert.Equal("QuillForge, Zephyr Studio", VocabularyPromptBuilder.Build(settings));
    }

    [Fact]
    public void BudgetCountsUtf8BytesAndNeverCutsAWord()
    {
        var first = new string('ž', 98); // 196 bytes, leaving room for separator + one ASCII term.
        var settings = new AppSettings { CustomVocabulary = [first, "TooLong", "X"] };
        Assert.Equal(first + ", X", VocabularyPromptBuilder.Build(settings));
    }

    [Fact]
    public void OversizedTermDoesNotPreventFollowingNamesFromBeingUsed()
    {
        var settings = new AppSettings { CustomVocabulary = [new string('x', 201), "QuillForge"] };
        Assert.Equal("QuillForge", VocabularyPromptBuilder.Build(settings));
    }

    [Theory]
    [InlineData(false, false, "en", ModelProfile.LargeTurbo)]
    [InlineData(true, true, "en", ModelProfile.LargeTurbo)]
    [InlineData(true, false, "hr", ModelProfile.LargeTurbo)]
    [InlineData(true, false, "de", ModelProfile.LargeTurbo)]
    [InlineData(true, false, "en", ModelProfile.CloudWhisperLargeV3Turbo)]
    [InlineData(true, false, "en", ModelProfile.SenseVoice)]
    public void DisabledOrUnsupportedModesReceiveNoEnglishHint(bool enabled, bool auto, string language, ModelProfile model)
    {
        var settings = new AppSettings
        {
            UseCustomVocabulary = enabled, AutoDetectLanguage = auto,
            Language = language, ModelProfile = model, CustomVocabulary = ["QuillForge"]
        };
        Assert.Null(VocabularyPromptBuilder.Build(settings));
    }

    [Fact]
    public void ExplicitlyEmptyVocabularyProducesNoHint()
    {
        Assert.Null(VocabularyPromptBuilder.Build(new AppSettings { CustomVocabulary = [] }));
    }

    [Fact]
    public void CloudTermsPutUserAdditionsFirstAndStopAtFifty()
    {
        var settings = new AppSettings { CustomVocabulary = [.. DefaultVocabulary.CodingTerms, "Revori", "Kenshi"] };
        var terms = VocabularyPromptBuilder.BuildCloudTerms(settings)!;
        Assert.Equal(["Revori", "Kenshi"], terms.Take(2));
        Assert.Equal(Talkty.App.Constants.CloudMaxVocabularyTerms, terms.Count);
    }

    [Theory]
    [InlineData("hr", false, ModelProfile.CloudMaiTranscribe2)]
    [InlineData("en", true, ModelProfile.CloudMaiTranscribe2)]
    [InlineData("de", false, ModelProfile.LargeTurbo)]
    public void CloudTermsAreSpellingsNotAnEnglishPromptSoAnyLanguageGetsThem(string language, bool auto, ModelProfile model)
    {
        var settings = new AppSettings
        {
            Language = language, AutoDetectLanguage = auto, ModelProfile = model, CustomVocabulary = ["  Revori ", "revori", "Athena\tAgent"]
        };
        Assert.Equal(["Revori", "Athena Agent"], VocabularyPromptBuilder.BuildCloudTerms(settings));
    }

    [Fact]
    public void DisabledOrEmptyVocabularySendsNoCloudTerms()
    {
        Assert.Null(VocabularyPromptBuilder.BuildCloudTerms(new AppSettings { UseCustomVocabulary = false, CustomVocabulary = ["Revori"] }));
        Assert.Null(VocabularyPromptBuilder.BuildCloudTerms(new AppSettings { CustomVocabulary = [] }));
    }

    [Fact]
    public void MissingVocabularyGetsBoundedDefaults()
    {
        var prompt = VocabularyPromptBuilder.Build(new AppSettings())!;
        Assert.Contains("Claude", prompt);
        Assert.Contains("PostgreSQL", prompt);
        Assert.True(Encoding.UTF8.GetByteCount(prompt) <= 200);
    }
}
