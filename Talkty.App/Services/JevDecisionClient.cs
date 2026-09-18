using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Talkty.App.Services;

/// <summary>
/// Which decision primitive a question uses. Score is deliberately not implemented — nothing in
/// Talkty needs a rubric position, and an unused primitive is untested surface.
/// </summary>
public enum JevQuestionKind
{
    /// <summary>Select exactly one of the supplied options. Returns choice + probabilities + confidence.</summary>
    Choice,
    /// <summary>Is one specific proposition supported? Returns a yes probability and NO confidence field.</summary>
    Noul
}

/// <summary>Outcome of one decision request, kept distinct so the record shows the real cause.</summary>
public enum JevStatus
{
    /// <summary>A valid, fully validated answer set came back.</summary>
    Evaluated,
    /// <summary>Transport/provider problem (network, HTTP status, timeout, oversized response).</summary>
    Unavailable,
    /// <summary>A response arrived but failed validation (unknown option, bad distribution, wrong model, missing usage).</summary>
    Invalid,
    /// <summary>Not sent: the request would exceed the local size bound.</summary>
    TooLarge,
    /// <summary>Not sent: the local daily reservation budget or rate limit is exhausted.</summary>
    BudgetExhausted,
    /// <summary>Not sent: the feature is off or no API key is configured.</summary>
    Disabled,
    /// <summary>The caller cancelled before an answer was validated.</summary>
    Cancelled
}

/// <summary>One bounded question. Criteria are the ONLY strings the model may answer with.</summary>
public sealed record JevQuestion(
    string Id,
    JevQuestionKind Kind,
    string Instructions,
    IReadOnlyDictionary<string, string> Criteria);

/// <summary>
/// One validated answer. <see cref="Choice"/>/<see cref="Confidence"/>/<see cref="Probabilities"/>
/// are set for Choice questions; <see cref="Noul"/> is set for Noul questions. Noul has no
/// confidence — the yes probability is the only statistic it reports.
/// </summary>
public sealed record JevAnswer(
    string Id,
    string? Choice,
    double? Confidence,
    IReadOnlyDictionary<string, double>? Probabilities,
    double? Noul)
{
    /// <summary>Probability of the selected option (Choice only). Not a measured accuracy.</summary>
    public double SelectedProbability =>
        Choice != null && Probabilities != null && Probabilities.TryGetValue(Choice, out var p) ? p : 0;
}

/// <summary>A validated evaluation plus the usage/timing facts the record needs.</summary>
public sealed record JevEvaluation(
    string Model,
    IReadOnlyDictionary<string, JevAnswer> Answers,
    long InputTokens,
    long OutputTokens,
    /// <summary>Null when the provider reported no cost — never fabricated as zero.</summary>
    double? CostUsd,
    long ElapsedMs);

/// <summary>
/// Result of an evaluation attempt. <see cref="FailureCode"/> is a short machine code
/// (never a provider response body) so it is safe to log and persist.
/// </summary>
public sealed record JevResult(JevStatus Status, JevEvaluation? Evaluation, string? FailureCode)
{
    public static JevResult Fail(JevStatus status, string code) => new(status, null, code);
}

/// <summary>
/// Transport and strict response validation for TypeSafe Jev decisions over OpenRouter.
///
/// This client cannot generate text. It sends a state object plus caller-defined questions and
/// accepts ONLY answers drawn from the options it supplied. Anything else — an unknown option, a
/// distribution that does not sum to one, a winning option that is not the maximum, an unexpected
/// model identifier, missing usage — is rejected as <see cref="JevStatus.Invalid"/> rather than
/// being half-trusted.
///
/// Route facts (live-qualified 2026-09-18 in ADE, re-qualified here): the endpoint is
/// <c>/api/alpha/decisions</c>. OpenRouter's GENERATED OpenAPI server/path pair composes
/// <c>/api/v1/api/alpha/decisions</c>, which 404s — do not "fix" the constant to match the docs.
/// The request asks for <c>typesafe/jev-1.13</c> and a successful response identifies itself with
/// the dated build <c>typesafe/jev-1.13-20260917</c>; both are allow-listed explicitly and an
/// arbitrary future alias is refused.
/// </summary>
public sealed class JevDecisionClient
{
    /// <summary>Live-qualified decisions route. NOT the chat-completions endpoint.</summary>
    public const string Endpoint = "https://openrouter.ai/api/alpha/decisions";

    /// <summary>Model slug we request.</summary>
    public const string Model = "typesafe/jev-1.13";

    /// <summary>
    /// Exact model identifiers a response may carry. The dated build is what the live route
    /// actually returns; arbitrary future versions are rejected so a silent model swap cannot
    /// invalidate our threshold qualification unnoticed.
    /// </summary>
    private static readonly string[] AcceptedModels =
    {
        "typesafe/jev-1.13",
        "typesafe/jev-1.13-20260917",
        "typesafe/jev-1.13.0",
        "jev-1.13",
        "jev-1.13.0",
    };

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // No redirects: a decisions POST must never be replayed to another host.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10)
        };
        return new HttpClient(handler)
        {
            // Hard ceiling; the real per-attempt budget is the linked CancellationToken below.
            Timeout = TimeSpan.FromMilliseconds(Constants.JevDecisionTimeoutMs + 5_000)
        };
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Serializes the request body, enforcing the question contract and the local size bound.
    /// Returns null (with a failure code) rather than sending something the model would reject.
    /// </summary>
    internal static (string? json, string? failure) BuildRequest(
        JsonNode state, IReadOnlyList<JevQuestion> questions)
    {
        if (questions.Count == 0 || questions.Count > Constants.JevMaxQuestions)
            return (null, "invalid_questions");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var questionNode = new JsonObject();

        foreach (var q in questions)
        {
            if (string.IsNullOrWhiteSpace(q.Id) || !seen.Add(q.Id))
                return (null, "invalid_questions");
            if (string.IsNullOrWhiteSpace(q.Instructions))
                return (null, "invalid_questions");

            bool valid = q.Kind switch
            {
                // Noul is a single proposition: exactly a true and a false criterion.
                JevQuestionKind.Noul => q.Criteria.Count == 2
                                        && q.Criteria.ContainsKey("true")
                                        && q.Criteria.ContainsKey("false"),
                JevQuestionKind.Choice => q.Criteria.Count >= 2 && q.Criteria.Count <= 255,
                _ => false
            };
            if (!valid || q.Criteria.Any(c => string.IsNullOrWhiteSpace(c.Key) || string.IsNullOrWhiteSpace(c.Value)))
                return (null, "invalid_questions");

            var criteria = new JsonObject();
            foreach (var (key, description) in q.Criteria)
                criteria[key] = description;

            questionNode[q.Id] = new JsonObject
            {
                ["type"] = q.Kind == JevQuestionKind.Noul ? "noul" : "choice",
                ["instructions"] = q.Instructions,
                ["criteria"] = criteria
            };
        }

        var body = new JsonObject
        {
            ["model"] = Model,
            ["state"] = state,
            ["questions"] = questionNode,
            // Pinned privacy posture. Accepting these settings is not an independent audit of the
            // provider's retention practice — it is the strictest posture the route offers us.
            ["provider"] = new JsonObject
            {
                ["data_collection"] = "deny",
                ["zdr"] = true,
                ["allow_fallbacks"] = false
            }
        };

        var json = body.ToJsonString(SerializerOptions);
        if (Encoding.UTF8.GetByteCount(json) > Constants.JevMaxRequestBytes)
            return (null, "input_too_large");

        return (json, null);
    }

    /// <summary>
    /// Validates a response against the questions we actually asked. Every failure returns a short
    /// code; no part of the provider body is propagated.
    /// </summary>
    internal static (JevEvaluation? evaluation, string? failure) Parse(
        string body, IReadOnlyList<JevQuestion> questions, long elapsedMs)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return (null, "invalid_json"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, "invalid_json");

            if (!root.TryGetProperty("model", out var modelEl) || modelEl.ValueKind != JsonValueKind.String)
                return (null, "missing_model");
            var model = modelEl.GetString()!;
            if (!AcceptedModels.Contains(model, StringComparer.Ordinal))
                return (null, "unexpected_model");

            if (!root.TryGetProperty("answers", out var answersEl) || answersEl.ValueKind != JsonValueKind.Object)
                return (null, "missing_answers");
            if (answersEl.EnumerateObject().Count() != questions.Count)
                return (null, "mismatched_answers");

            var answers = new Dictionary<string, JevAnswer>(StringComparer.Ordinal);
            foreach (var question in questions)
            {
                if (!answersEl.TryGetProperty(question.Id, out var a) || a.ValueKind != JsonValueKind.Object)
                    return (null, "missing_answer");

                var expectedType = question.Kind == JevQuestionKind.Noul ? "noul" : "choice";
                if (!a.TryGetProperty("type", out var typeEl) ||
                    typeEl.ValueKind != JsonValueKind.String ||
                    !string.Equals(typeEl.GetString(), expectedType, StringComparison.Ordinal))
                    return (null, "mismatched_type");

                if (question.Kind == JevQuestionKind.Noul)
                {
                    // Noul reports a yes probability only. There is no confidence statistic here;
                    // reading one in would invent a number the model never produced.
                    if (!TryProbability(a, "noul", out var noul))
                        return (null, "invalid_probability");
                    answers[question.Id] = new JevAnswer(question.Id, null, null, null, noul);
                    continue;
                }

                if (!a.TryGetProperty("choice", out var choiceEl) || choiceEl.ValueKind != JsonValueKind.String)
                    return (null, "missing_choice");
                var choice = choiceEl.GetString()!;
                if (!question.Criteria.ContainsKey(choice))
                    return (null, "unknown_choice");

                if (!TryProbability(a, "confidence", out var confidence))
                    return (null, "invalid_probability");

                if (!a.TryGetProperty("probabilities", out var probsEl) || probsEl.ValueKind != JsonValueKind.Object)
                    return (null, "missing_probabilities");

                var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
                double sum = 0;
                foreach (var key in question.Criteria.Keys)
                {
                    if (!TryProbability(probsEl, key, out var p))
                        return (null, "missing_probability");
                    probabilities[key] = p;
                    sum += p;
                }
                if (probsEl.EnumerateObject().Count() != question.Criteria.Count)
                    return (null, "mismatched_probabilities");
                if (Math.Abs(sum - 1.0) > 0.005)
                    return (null, "invalid_distribution");

                // The selected option must actually be the maximum, or the distribution and the
                // choice disagree and neither can be trusted as a threshold input.
                var selected = probabilities[choice];
                if (probabilities.Values.Any(p => p > selected + 0.00001))
                    return (null, "choice_not_maximum");

                answers[question.Id] = new JevAnswer(question.Id, choice, confidence, probabilities, null);
            }

            if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                return (null, "missing_usage");

            var (inputTokens, inputFailure) = UsageCount(usage, "input_tokens", "inputTokens");
            if (inputFailure != null) return (null, inputFailure);
            var (outputTokens, outputFailure) = UsageCount(usage, "output_tokens", "outputTokens");
            if (outputFailure != null) return (null, outputFailure);

            // Missing cost stays UNKNOWN. Treating it as zero would quietly under-count spending.
            double? cost = null;
            if (usage.TryGetProperty("cost", out var costEl) && costEl.ValueKind != JsonValueKind.Null)
            {
                if (costEl.ValueKind != JsonValueKind.Number || !costEl.TryGetDouble(out var c) ||
                    !double.IsFinite(c) || c < 0)
                    return (null, "invalid_cost");
                cost = c;
            }

            return (new JevEvaluation(model, answers, inputTokens, outputTokens, cost, elapsedMs), null);
        }
    }

    private static bool TryProbability(JsonElement parent, string name, out double value)
    {
        value = 0;
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return false;
        if (!el.TryGetDouble(out var d) || !double.IsFinite(d) || d < 0 || d > 1)
            return false;
        value = d;
        return true;
    }

    /// <summary>
    /// Reads a usage counter that the documentation and the live route spell differently.
    /// Both spellings present with different values is a contradiction, not a value to pick from.
    /// </summary>
    private static (long value, string? failure) UsageCount(JsonElement usage, string snake, string camel)
    {
        var hasSnake = usage.TryGetProperty(snake, out var snakeEl) && snakeEl.ValueKind == JsonValueKind.Number;
        var hasCamel = usage.TryGetProperty(camel, out var camelEl) && camelEl.ValueKind == JsonValueKind.Number;

        if (hasSnake && hasCamel && snakeEl.GetRawText() != camelEl.GetRawText())
            return (0, "conflicting_usage");

        var el = hasSnake ? snakeEl : hasCamel ? camelEl : default;
        if (!hasSnake && !hasCamel) return (0, "missing_usage");
        if (!el.TryGetInt64(out var v) || v < 0) return (0, "missing_usage");
        return (v, null);
    }

    /// <summary>
    /// Sends one decision request. Never throws for transport problems — the caller's flow must
    /// survive a provider outage unchanged.
    /// </summary>
    public async Task<JevResult> EvaluateAsync(
        string apiKey,
        JsonNode state,
        IReadOnlyList<JevQuestion> questions,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return JevResult.Fail(JevStatus.Disabled, "no_api_key");

        var (json, buildFailure) = BuildRequest(state, questions);
        if (json == null)
            return JevResult.Fail(
                buildFailure == "input_too_large" ? JevStatus.TooLarge : JevStatus.Invalid,
                buildFailure ?? "invalid_request");

        using var timeoutCts = new CancellationTokenSource(Constants.JevDecisionTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.TryAddWithoutValidation("X-Title", "Talkty prompt fidelity");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                Log.Warning($"Jev decision HTTP {code}");
                // 401/402 are key-level and identical on every retry — the caller reports them
                // as unavailable and does not build a retry chain around them.
                return JevResult.Fail(JevStatus.Unavailable, $"http_{code}");
            }

            var body = await ReadBoundedAsync(response, linkedCts.Token);
            if (body == null)
                return JevResult.Fail(JevStatus.Unavailable, "response_too_large");

            sw.Stop();
            var (evaluation, failure) = Parse(body, questions, sw.ElapsedMilliseconds);
            if (evaluation == null)
            {
                Log.Warning($"Jev decision rejected: {failure}");
                return JevResult.Fail(JevStatus.Invalid, failure ?? "invalid_response");
            }

            Log.Debug($"Jev decision ok in {sw.ElapsedMilliseconds}ms " +
                      $"({evaluation.InputTokens} in / {evaluation.OutputTokens} out, " +
                      $"cost {(evaluation.CostUsd.HasValue ? evaluation.CostUsd.Value.ToString("G") : "unknown")})");
            return new JevResult(JevStatus.Evaluated, evaluation, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return JevResult.Fail(JevStatus.Cancelled, "cancelled");
        }
        catch (OperationCanceledException)
        {
            Log.Warning($"Jev decision timed out after {Constants.JevDecisionTimeoutMs}ms");
            return JevResult.Fail(JevStatus.Unavailable, "timeout");
        }
        catch (Exception ex)
        {
            Log.Warning($"Jev decision transport failed: {ex.GetType().Name}");
            return JevResult.Fail(JevStatus.Unavailable, "network_unavailable");
        }
    }

    /// <summary>Reads the body with a hard byte cap so a runaway response cannot exhaust memory.</summary>
    private static async Task<string?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > Constants.JevMaxResponseBytes)
                return null;
            ms.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
