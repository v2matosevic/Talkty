using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Talkty.App.Services;

/// <summary>
/// Expands a raw voice transcription into a complete, structured prompt for a coding AI agent,
/// using an LLM via OpenRouter's chat-completions endpoint. Runs only when the user enables
/// "Prompting" on the recording overlay.
///
/// Uses a model FALLBACK CHAIN: the primary model is tried first; on any failure (unavailable,
/// rate-limited, timeout, error, empty) it falls back to the next, then the next. This keeps the
/// feature working even when one provider is degraded.
///
/// See docs/PROMPTING.md for the design rationale and the system-prompt engineering notes.
/// </summary>
public class PromptRefinementService : IPromptRefinementService
{
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";

    // Fallback chain (primary first). All are fast, cheap, NON-reasoning instruct models — a
    // dictation→prompt rewrite is instruction-following, not problem-solving, so reasoning models
    // would only add "thinking" latency for marginal gain. Verify slugs against OpenRouter if a
    // call returns 404 and the chain skips a model.
    private static readonly string[] Models =
    {
        // Ordered FASTEST-FIRST for a European user. GLM 4.7 Flash measured ~15.8s in testing
        // (Z.ai's servers are far from the EU — the same geography tax we saw on transcription),
        // so it's demoted to last resort. Gemini (Google EU edge) leads. See docs/PROMPTING.md.
        "google/gemini-3.1-flash-lite", // primary    — fastest TTFT, Google EU edge → low EU latency
        "deepseek/deepseek-v4-flash",   // fallback 1  — ultra-cheap, fast
        "z-ai/glm-4.7-flash",           // fallback 2  — strong quality, but ~16s from EU; last resort
    };

    // The meta-prompt that defines the feature. Grounded in Anthropic's Claude Code best-practices
    // guidance plus current coding-agent prompting practice: scale structure to request size, adapt to
    // request TYPE (bug/feature/refactor/question), verification with root-cause, follow existing
    // patterns, plan-before-code for non-trivial work, anti-over-engineering, faithful-not-inventive.
    // Keep this in sync with docs/PROMPTING.md (the human-readable source of truth).
    private const string SystemPrompt =
        """
        You are an expert prompt engineer specializing in prompts for autonomous coding agents
        (Claude Code, Cursor, GitHub Copilot). A developer dictated a request by voice; you receive the
        raw transcription. Produce the precise, well-formed prompt they would have written by hand.

        SCALE THE OUTPUT TO THE REQUEST. First judge how big the request is and match the structure to it:
        - Trivial / one-liner (rename a symbol, add a log line, fix a typo, tweak a value): output a single
          direct imperative sentence naming the exact target. Do NOT add headings or sections — over-
          structuring a small ask just wastes the agent's time.
        - Substantial (a feature, a multi-file change, anything non-obvious): use the full structure below.
        Never pad a small request into a big template.

        STRUCTURE (substantial requests) — markdown sections, include ONLY those the input supports:
        - **Task**: the goal in one or two imperative sentences.
        - **Context**: the stack, the specific files / paths / components / functions involved, and any
          existing patterns to follow. Name them exactly as the developer did.
        - **Requirements**: specific, actionable steps as a bullet list.
        - **Constraints**: what must not change, libraries not to add, scope not to creep into.
        - **Verify**: how the agent should prove it worked — a named test, a build, or expected behavior.

        ADAPT TO THE KIND OF REQUEST:
        - Bug fix: state the symptom and the likely location (if given); instruct the agent to write a
          failing test that reproduces it, then fix the ROOT CAUSE (do not suppress the symptom), and
          confirm the test passes.
        - Feature: if it spans multiple files or the approach is uncertain, instruct the agent to explore
          the relevant code and propose a short plan before implementing. If it is small and clear, tell it
          to implement directly.
        - Refactor: scope it tightly, require behavior to stay identical, keep it isolated from feature/bug
          changes.
        - Question / investigation: output a clear question pointing at the relevant files; do NOT add
          implementation scaffolding.

        PRINCIPLES:
        - Faithful, not inventive. Capture exactly what was asked. Never add features, libraries,
          dependencies, or scope the developer did not mention. If a load-bearing detail is genuinely
          ambiguous, add a short "> Clarify: ..." note for the developer instead of guessing.
        - Preserve every technical identifier verbatim — file names, paths, function/variable names, tools,
          frameworks (kubectl, PostgreSQL, React, TypeScript).
        - Prefer existing patterns. Tell the agent to follow conventions already in the codebase and reuse
          existing utilities rather than introducing new ones.
        - Avoid over-engineering — no speculative abstraction, defensive code, or tests for cases that
          cannot occur. Keep diffs minimal and focused.
        - Be direct and concise. Imperative voice, no filler or flattery. ~150-300 words for substantial
          requests; far less for small ones.

        Output ONLY the final prompt text, ready to paste into the coding agent — no preamble, no
        explanation of your changes, no surrounding quotes, and no wrapping code fences.
        """;

    // One HttpClient for the service lifetime — per-request creation exhausts sockets.
    private static readonly HttpClient Http = new()
    {
        // Hard ceiling per attempt; the real per-attempt budget is the linked CancellationToken.
        Timeout = TimeSpan.FromMilliseconds(Constants.PromptRefinementTimeoutMs + 10_000)
    };

    private readonly object _lock = new();
    private string? _apiKey;

    public bool IsConfigured
    {
        get { lock (_lock) { return !string.IsNullOrWhiteSpace(_apiKey); } }
    }

    public void SetApiKey(string? apiKey)
    {
        lock (_lock) { _apiKey = apiKey?.Trim(); }
    }

    public async Task<string?> RefineAsync(string transcription, CancellationToken cancellationToken = default)
    {
        string? key;
        lock (_lock) { key = _apiKey; }

        if (string.IsNullOrWhiteSpace(key))
        {
            Log.Warning("PromptRefinement: no API key configured");
            return null;
        }
        if (string.IsNullOrWhiteSpace(transcription))
            return null;

        for (int i = 0; i < Models.Length; i++)
        {
            // ESC cancels the whole chain (don't try the next model after a user cancel).
            if (cancellationToken.IsCancellationRequested)
            {
                Log.Info("PromptRefinement cancelled (ESC)");
                return null;
            }

            var model = Models[i];
            var (ok, content) = await TryRefineWithModel(model, key!, transcription, cancellationToken);
            if (ok && !string.IsNullOrWhiteSpace(content))
            {
                if (i > 0)
                    Log.Info($"Prompt refined via fallback model #{i + 1} ({model})");
                return content!.Trim();
            }

            var next = i < Models.Length - 1 ? $"falling back to '{Models[i + 1]}'" : "no more fallbacks";
            Log.Warning($"Refinement model '{model}' failed/empty — {next}");
        }

        Log.Error("All refinement models failed — caller falls back to raw transcription");
        return null;
    }

    /// <summary>
    /// Single attempt against one model. Returns (true, content) on success, (false, null) on any
    /// failure (HTTP error, timeout, parse error, empty) so the chain can move to the next model.
    /// </summary>
    private async Task<(bool ok, string? content)> TryRefineWithModel(
        string model, string apiKey, string transcription, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(Constants.PromptRefinementTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var payload = new
            {
                model,
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = transcription }
                },
                // Low temperature — faithful, near-deterministic rewrite, not creativity.
                temperature = 0.3
            };

            var json = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, linkedCts.Token);
            var body = await response.Content.ReadAsStringAsync(linkedCts.Token);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning($"Refinement '{model}' HTTP {(int)response.StatusCode}: {Truncate(body, 200)}");
                return (false, null);
            }

            var content = ExtractContent(body);
            if (string.IsNullOrWhiteSpace(content))
            {
                Log.Warning($"Refinement '{model}' returned empty content");
                return (false, null);
            }

            Log.Info($"Refinement '{model}' ok in {sw.ElapsedMilliseconds}ms: {transcription.Length} → {content.Length} chars");
            return (true, content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.Info($"Refinement '{model}' cancelled (ESC)");
            return (false, null);
        }
        catch (OperationCanceledException)
        {
            Log.Warning($"Refinement '{model}' timed out after {Constants.PromptRefinementTimeoutMs / 1000}s");
            return (false, null);
        }
        catch (Exception ex)
        {
            Log.Warning($"Refinement '{model}' failed: {ex.Message}");
            return (false, null);
        }
    }

    /// <summary>Pulls the assistant message out of an OpenAI/OpenRouter chat-completions response.</summary>
    private static string? ExtractContent(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var msg = choices[0].GetProperty("message");
                if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    return content.GetString();
            }
            Log.Warning($"Refinement response had no choices[0].message.content: {Truncate(body, 200)}");
            return null;
        }
        catch (JsonException ex)
        {
            Log.Warning($"Failed to parse refinement response: {ex.Message}");
            return null;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
