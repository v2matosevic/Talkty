using System.Linq;

namespace Talkty.App.Services;

/// <summary>
/// Turns a validated Jev evaluation into at most a couple of concerns worth the speaker's
/// attention. Kept separate from transport and orchestration so the thresholds can be tuned and
/// tested on their own.
///
/// The thresholds are PROVISIONAL pilot values chosen to favour precision: a false alarm on a
/// faithful rewrite is the failure that would make people turn the feature off, so an uncertain
/// judgment stays silent. They are not a measured error rate, and a Choice confidence is a
/// distribution statistic, not a probability that the answer is correct.
/// </summary>
public static class PromptFidelityPolicy
{
    /// <summary>
    /// Selects the concerns that clear the thresholds, ordered by severity. Everything the model
    /// answered with <c>preserved</c> or <c>unclear</c>, or answered below threshold, is dropped.
    /// </summary>
    public static IReadOnlyList<FidelityConcern> Interpret(
        JevEvaluation evaluation, IReadOnlyList<TranscriptClause> clauses)
    {
        var byId = clauses.ToDictionary(c => c.Id, c => c.Text, StringComparer.Ordinal);

        // The clause the model independently named as most at risk. Used only to ORDER concerns,
        // never to create one on its own — a single Choice among clauses always has to pick
        // something, so on a faithful rewrite it is answered by the 'none' option.
        string? attention = null;
        if (evaluation.Answers.TryGetValue(PromptFidelityService.AttentionQuestionId, out var att) &&
            att.Choice is { } choice && choice != PromptFidelityService.NoneOption &&
            att.SelectedProbability >= Constants.JevFidelityMinProbability &&
            att.Confidence >= Constants.JevFidelityMinConfidence)
        {
            attention = choice;
        }

        var concerns = new List<FidelityConcern>();

        foreach (var clause in clauses)
        {
            if (!evaluation.Answers.TryGetValue(clause.Id, out var answer) || answer.Choice == null)
                continue;
            if (answer.Choice is not ("omitted" or "contradicted"))
                continue;
            if (answer.SelectedProbability < Constants.JevFidelityMinProbability)
                continue;
            if (answer.Confidence is not { } confidence || confidence < Constants.JevFidelityMinConfidence)
                continue;

            var contradicted = answer.Choice == "contradicted";
            concerns.Add(new FidelityConcern(
                contradicted ? FidelityConcernKind.ContradictedClause : FidelityConcernKind.OmittedClause,
                contradicted ? PromptFidelityAnalyzer.LabelContradictedClause : PromptFidelityAnalyzer.LabelOmittedClause,
                byId.TryGetValue(clause.Id, out var text) ? text : string.Empty,
                clause.Id,
                answer.SelectedProbability,
                confidence));
        }

        // A contradiction outranks an omission; the clause the model flagged for attention outranks
        // the rest of its own kind; ties break on the selected probability.
        var ordered = concerns
            .OrderByDescending(c => c.Kind == FidelityConcernKind.ContradictedClause)
            .ThenByDescending(c => c.ClauseId == attention)
            .ThenByDescending(c => c.Probability ?? 0)
            .ToList();

        // Requirements the rewrite invented. Noul reports a yes probability and has no confidence
        // statistic, so the probability threshold is the only gate — set higher for that reason.
        if (evaluation.Answers.TryGetValue(PromptFidelityService.AddedQuestionId, out var added) &&
            added.Noul is { } yes && yes >= Constants.JevFidelityMinAddedProbability)
        {
            ordered.Add(new FidelityConcern(
                FidelityConcernKind.AddedRequirement,
                PromptFidelityAnalyzer.LabelAddedRequirement,
                string.Empty,
                null,
                yes));
        }

        return ordered.Take(Constants.JevMaxSurfacedConcerns).ToList();
    }
}
