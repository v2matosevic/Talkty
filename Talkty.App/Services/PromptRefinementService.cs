using System.Linq;
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
/// A COMPLETENESS GUARD treats a suspiciously-short result (a model that summarized instead of
/// expanding) as a failure too, escalating to the next model. This lets cheaper/lower-tier models be
/// trusted as the primary: if one drops detail, the chain automatically recovers to a stronger model.
///
/// See docs/PROMPTING.md for the design rationale and the system-prompt engineering notes.
/// </summary>
public class PromptRefinementService : IPromptRefinementService
{
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";

    // Default model chain (primary first). All are NON-reasoning instruct models — a dictation→prompt
    // rewrite is instruction-following, not problem-solving, so reasoning models would only add
    // "thinking" latency for marginal gain. Verify slugs against OpenRouter if a call 404s and the
    // chain skips a model.
    //
    // The user can override the PRIMARY via Settings (SetModel); the runtime chain then puts their
    // choice first and keeps the rest of these as automatic fallbacks (see BuildChain).
    private static readonly string[] DefaultModels =
    {
        // Default primary is minimax/minimax-m3 — top-tier instruction-following (Artificial Analysis
        // Intelligence 44, above many closed models) at a flash-tier price, chosen for FIDELITY on the
        // completeness-critical rewrite. It is multi-hosted on OpenRouter at very different speeds, so
        // we pin provider sort=throughput (in the payload) to land on a fast host (Together/Makora
        // ~90-100 t/s) and avoid a slow route reintroducing a GLM-style latency tax.
        //
        // The FIRST fallback is gemini-3.5-flash — the most reliable expander in real logs (it never
        // timed out and always grew the input), so when the primary times out OR trips the completeness
        // guard (summarizes), the chain escalates straight to a high-quality model rather than a weaker
        // one. The remaining fallbacks are the fast/cheap tiers for a last resort.
        "minimax/minimax-m3",           // default primary — top instruction-following (AA 44), non-reasoning; provider-pinned for speed
        "google/gemini-3.5-flash",      // fallback #1     — most reliable expander in logs; quality escalation target for the guard
        "google/gemini-3.1-flash-lite", // fallback #2     — fastest, lowest latency, Google EU edge
        "deepseek/deepseek-v4-flash",   // fallback #3     — ultra-cheap last resort
    };

    // The meta-prompt that defines the feature. Grounded in Anthropic's Claude Code best-practices
    // guidance plus current coding-agent prompting practice. The headline rule is COMPLETENESS:
    // reformat the dictation into a prompt WITHOUT dropping any of the speaker's concrete details —
    // omission was the #1 quality complaint. Then: scale structure (not content) to request size,
    // adapt to request TYPE (bug/feature/refactor/question), verification with root-cause, follow
    // existing patterns, plan-before-code for non-trivial work, anti-over-engineering, faithful-and-
    // complete (add nothing, drop nothing). Keep this in sync with docs/PROMPTING.md (source of truth).
    private const string SystemPrompt =
        """
        You are an expert prompt engineer specializing in prompts for autonomous coding agents
        (Claude Code, Cursor, GitHub Copilot). A developer dictated a request out loud; you receive the
        raw speech-to-text transcription. Rewrite it into the precise, well-formed prompt they would have
        typed by hand — capturing EVERYTHING they said.

        YOUR #1 JOB IS TO LOSE NOTHING. Dictation rambles, but buried in the rambling are concrete,
        load-bearing details: specific file / function / variable names, exact values, ordering ("do X
        before Y"), preferences ("use the existing helper"), edge cases ("and if the list is empty..."),
        and throwaway asides ("oh, and make sure it still works on mobile"). Those small things are the
        whole point of the request. You are REFORMATTING speech into a prompt, NOT summarizing it. If the
        developer said something that carries any instruction, requirement, constraint, preference,
        example, value, or caveat, it MUST survive into your output. When in doubt, keep it.

        WHAT YOU MAY REMOVE — only pure speech noise that carries no instruction:
        - disfluencies and fillers ("um", "uh", "like", "you know", "I mean", "basically", "sort of");
        - repetitions and restarts of the same point;
        - thinking-out-loud that the speaker then abandons.
        Resolve self-corrections by keeping the FINAL intent and dropping the abandoned one ("make it red,
        no wait, blue" → blue). Never mistake a real detail for filler — when unsure whether a phrase is
        noise or substance, treat it as substance and keep it.

        SCALE THE STRUCTURE (NOT THE CONTENT) TO THE REQUEST:
        - Trivial / one-liner (rename a symbol, add a log line, fix a typo, tweak one value): output a
          single direct imperative sentence naming the exact target. Do NOT add headings — over-
          structuring a tiny ask just wastes the agent's time.
        - Substantial (a feature, a multi-file change, multiple requirements, anything non-obvious): use the
          markdown structure below.
        Scaling controls how much SCAFFOLDING you add, never how much of the developer's content you keep.
        A one-liner has little to lose; a detailed ask must keep all of it even if the prompt ends up long.

        STRUCTURE (substantial requests) — markdown sections, include ONLY those the input supports:
        - **Task**: the goal in one or two imperative sentences.
        - **Context**: the stack, the specific files / paths / components / functions involved, and any
          existing patterns to follow. Name them exactly as the developer did.
        - **Requirements**: every distinct thing they asked for, as actionable bullets. This is where the
          small details live — one bullet per detail; never merge two separate asks into one vague bullet.
        - **Constraints**: what must not change, libraries not to add, scope not to creep into.
        - **Notes**: any detail, preference, or aside that matters but doesn't fit the sections above. Never
          drop an aside just because it has no obvious home — put it here.
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
        - Faithful AND complete. Add nothing the developer did not say (no new features, libraries,
          dependencies, or scope); drop nothing they did. If a load-bearing detail is genuinely ambiguous,
          add a short "> Clarify: ..." note instead of guessing OR silently dropping it.
        - Preserve every technical identifier verbatim — file names, paths, function/variable names, tools,
          frameworks (kubectl, PostgreSQL, React, TypeScript).
        - Prefer existing patterns. Tell the agent to follow conventions already in the codebase and reuse
          existing utilities rather than introducing new ones.
        - Avoid over-engineering — no speculative abstraction, defensive code, or tests for cases that
          cannot occur. This trims only YOUR additions; it never licenses dropping the developer's own
          requirements.
        - Be direct: imperative voice, no filler or flattery. Let length follow the input — short asks stay
          short, detailed asks become a thorough prompt. Do NOT compress to hit a word count.

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
    private string? _primaryModel;

    public bool IsConfigured
    {
        get { lock (_lock) { return !string.IsNullOrWhiteSpace(_apiKey); } }
    }

    public void SetApiKey(string? apiKey)
    {
        lock (_lock) { _apiKey = apiKey?.Trim(); }
    }

    public void SetModel(string? modelSlug)
    {
        lock (_lock) { _primaryModel = string.IsNullOrWhiteSpace(modelSlug) ? null : modelSlug.Trim(); }
    }

    /// <summary>
    /// Builds the effective model chain: the user-chosen primary first (if any), then the default
    /// models as fallbacks (de-duplicated). Falls back to the defaults when no override is set.
    /// </summary>
    private string[] BuildChain()
    {
        string? primary;
        lock (_lock) { primary = _primaryModel; }

        if (string.IsNullOrWhiteSpace(primary) ||
            string.Equals(primary, DefaultModels[0], StringComparison.OrdinalIgnoreCase))
            return DefaultModels;

        var rest = DefaultModels.Where(m => !string.Equals(m, primary, StringComparison.OrdinalIgnoreCase));
        return new[] { primary }.Concat(rest).ToArray();
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

        var chain = BuildChain();
        for (int i = 0; i < chain.Length; i++)
        {
            // ESC cancels the whole chain (don't try the next model after a user cancel).
            if (cancellationToken.IsCancellationRequested)
            {
                Log.Info("PromptRefinement cancelled (ESC)");
                return null;
            }

            var model = chain[i];
            bool isLast = i == chain.Length - 1;
            var (ok, content) = await TryRefineWithModel(model, key!, transcription, cancellationToken);
            if (ok && !string.IsNullOrWhiteSpace(content))
            {
                // Completeness guard: a model that returned a far-shorter-than-input result summarized
                // and dropped detail (the #1 quality complaint). Treat it as a failure and escalate to
                // the next model — UNLESS this is the last one, where a structured-but-short prompt
                // still beats raw transcription, so we keep it. Short inputs are never guarded.
                if (!isLast && IsSuspectedSummary(transcription, content!))
                {
                    Log.Warning(
                        $"Refinement model '{model}' summarized ({transcription.Length} → {content!.Trim().Length} chars, " +
                        $"below {Constants.PromptCompletenessMinOutputRatio:P0} of input) — escalating to '{chain[i + 1]}'");
                    continue;
                }

                if (i > 0)
                    Log.Info($"Prompt refined via fallback model #{i + 1} ({model})");
                return content!.Trim();
            }

            var next = !isLast ? $"falling back to '{chain[i + 1]}'" : "no more fallbacks";
            Log.Warning($"Refinement model '{model}' failed/empty — {next}");
        }

        Log.Error("All refinement models failed — caller falls back to raw transcription");
        return null;
    }

    /// <summary>
    /// True when a refinement looks like a SUMMARY rather than an expansion: the input was substantial
    /// (so we expected the prompt to grow) yet the output came back well under the input length. Pure
    /// length heuristic — cheap, model-agnostic, and tuned from real logs (see Constants). Short inputs
    /// are exempt because they legitimately produce short prompts.
    /// </summary>
    internal static bool IsSuspectedSummary(string input, string output)
    {
        if (input.Length < Constants.PromptCompletenessMinInputChars)
            return false;
        return output.Trim().Length < input.Length * Constants.PromptCompletenessMinOutputRatio;
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
                temperature = 0.2,
                // Headroom so a long, detail-complete prompt is never truncated by a provider's
                // default output cap. This is a ceiling, not a target — it adds no latency.
                max_tokens = 2048,
                // Route to the fastest-decoding provider. Critical for minimax/minimax-m3, which is
                // multi-hosted at very different speeds — a slow route would reintroduce a GLM-style
                // latency tax. Harmless no-op for single-provider models (Gemini/DeepSeek).
                provider = new { sort = "throughput" }
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
