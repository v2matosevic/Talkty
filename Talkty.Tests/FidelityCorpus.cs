using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Talkty.Tests;

/// <summary>One labeled dictation/rewrite pair from tools/jev-fidelity-corpus.json.</summary>
public sealed class FidelityCase
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("split")] public string Split { get; set; } = "";
    [JsonPropertyName("language")] public string Language { get; set; } = "";
    [JsonPropertyName("scenario")] public string Scenario { get; set; } = "";
    [JsonPropertyName("transcript")] public string Transcript { get; set; } = "";
    [JsonPropertyName("rewrite")] public string Rewrite { get; set; } = "";
    [JsonPropertyName("expectConcern")] public bool ExpectConcern { get; set; }
    [JsonPropertyName("expectedKinds")] public List<string> ExpectedKinds { get; set; } = new();
    [JsonPropertyName("expectedClause")] public string? ExpectedClause { get; set; }
    [JsonPropertyName("codeShouldCatch")] public bool CodeShouldCatch { get; set; }
    [JsonPropertyName("label")] public string Label { get; set; } = "";

    public bool IsDevelopment => Split == "development";
    public bool IsHoldout => Split == "holdout";
}

internal sealed class FidelityCorpusDocument
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("cases")] public List<FidelityCase> Cases { get; set; } = new();
}

/// <summary>
/// Loads the labeled corpus from the repository so the unit tests and the paid live qualification
/// script measure exactly the same cases. Labels were written before any live call; the holdout
/// split must never be used to choose a threshold.
/// </summary>
public static class FidelityCorpus
{
    private static readonly Lazy<List<FidelityCase>> Loaded = new(Load);

    public static IReadOnlyList<FidelityCase> Cases => Loaded.Value;

    public static string Path => System.IO.Path.Combine(RepositoryRoot(), "tools", "jev-fidelity-corpus.json");

    private static List<FidelityCase> Load()
    {
        var json = File.ReadAllText(Path);
        var doc = JsonSerializer.Deserialize<FidelityCorpusDocument>(json)
                  ?? throw new InvalidOperationException("Fidelity corpus did not deserialize.");
        if (doc.Cases.Count == 0)
            throw new InvalidOperationException("Fidelity corpus is empty.");
        return doc.Cases;
    }

    /// <summary>Walks up from the test binary until it finds the solution file.</summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Talkty.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Talkty.sln not found above the test binary.");
    }
}
