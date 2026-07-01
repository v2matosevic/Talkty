using Talkty.App.ViewModels;
using Xunit;

namespace Talkty.Tests;

/// <summary>
/// Locks in the "misheard => correct" line format used by the Settings text-replacements
/// editor: round-trip fidelity, separator tolerance, and malformed-line handling.
/// </summary>
public class ReplacementEditorTests
{
    [Fact]
    public void ParseReplacements_ParsesBasicLines()
    {
        var text = "cloud => Claude\ncube cuddle => kubectl";
        var result = SettingsViewModel.ParseReplacements(text);

        Assert.Equal(2, result.Count);
        Assert.Equal("Claude", result["cloud"]);
        Assert.Equal("kubectl", result["cube cuddle"]);
    }

    [Fact]
    public void ParseReplacements_AcceptsArrowSeparatorAndCrLf()
    {
        var text = "post gres -> PostgreSQL\r\nsequel => SQL\r\n";
        var result = SettingsViewModel.ParseReplacements(text);

        Assert.Equal(2, result.Count);
        Assert.Equal("PostgreSQL", result["post gres"]);
        Assert.Equal("SQL", result["sequel"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  \n")]
    [InlineData("no separator here")]
    [InlineData("=> value with no key")]
    [InlineData("key with no value =>")]
    public void ParseReplacements_SkipsBlankAndMalformedLines(string text)
    {
        Assert.Empty(SettingsViewModel.ParseReplacements(text));
    }

    [Fact]
    public void ParseReplacements_IsCaseInsensitiveOnKeys()
    {
        var result = SettingsViewModel.ParseReplacements("Cloud => Claude");
        Assert.True(result.ContainsKey("cloud"));
    }

    [Fact]
    public void FormatAndParse_RoundTrips()
    {
        var original = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cube cuddle"] = "kubectl",
            ["engine X"] = "nginx",
            ["jay son"] = "JSON"
        };

        var text = SettingsViewModel.FormatReplacements(original);
        var parsed = SettingsViewModel.ParseReplacements(text);

        Assert.Equal(original.Count, parsed.Count);
        foreach (var (k, v) in original)
            Assert.Equal(v, parsed[k]);
    }
}
