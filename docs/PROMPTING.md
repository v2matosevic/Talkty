# Prompting — turn dictation into a coding-agent prompt

The **Prompting** feature lets you speak a rough request and have Talkty hand you a clean,
well-structured prompt for a coding AI agent (Claude Code, Cursor, GitHub Copilot) instead of
the raw transcription.

You hover the recording pill, click **Prompting**, speak, and on stop the transcription is
expanded by an LLM into a paste-ready prompt.

---

## How it works

```
hotkey ─▶ record ─▶ transcribe (local or cloud)
                       │
                       ├─ TextPostProcessor (filler strip, coding-term fixes)   ← always
                       │
                       ├─ IF Prompting is on:
                       │     PromptRefinementService.RefineAsync()
                       │       → OpenRouter chat completions (model fallback chain)
                       │       → structured coding-agent prompt
                       │
                       └─▶ clipboard / auto-paste
```

- **Trigger:** a hover-revealed **sparkle icon** toggle on the recording pill (`OverlayWindow`) —
  a bare glyph matching the pill's wave-bar/timer style, tinted accent-purple when active. It is
  **per-recording** — it resets to off each time a new recording starts.
- **Layered cleanup:** the deterministic `TextPostProcessor` runs first (so coding terms like
  `kubectl` / `PostgreSQL` are already corrected and filler is stripped), then the LLM does the
  real expansion. Cheap deterministic pass feeding a smart rewrite.
- **Failure is safe:** if refinement fails for any reason, the raw (cleaned) transcription is used
  instead — you never lose your dictation.
- **Requires an OpenRouter key** (the same key used for cloud transcription — see
  [cloud transcription](#) / `project-cloud-transcription-openrouter`). Local transcription still
  works without a key; only the refinement step needs it.

---

## Model fallback chain

Refinement tries these models in order. On **any** failure of one (unavailable, rate-limited,
HTTP error, timeout, or empty response) it falls through to the next. If all fail, the raw
transcription is used.

| Order | Model (OpenRouter slug) | Price in/out ($/M) | Why |
|------|--------------------------|--------------------|-----|
| 1 (primary) | `minimax/minimax-m3` | $0.30 / $1.20 | Top-tier instruction-following (Artificial Analysis Intelligence **44**) at a flash-tier price; standard (non-reasoning) instruct model → chosen for **fidelity** on the completeness-critical rewrite. Provider-pinned for speed (see below). |
| 2 (fallback) | `google/gemini-3.1-flash-lite` | ~$0.25 / $1.50 | Fast TTFT, Google EU edge, instruction-tuned/extraction-optimized → low-latency safety net |
| 3 (last resort) | `deepseek/deepseek-v4-flash` | $0.09 / $0.18 | Ultra-cheap, fast |

> **Primary = fidelity, fallbacks = speed.** The completeness rewrite (preserve every detail) is
> instruction-following-heavy, so the primary is the strongest instruction-follower we can run cheaply:
> `minimax/minimax-m3` (Artificial Analysis Intelligence 44, a *non-reasoning* instruct model — no
> thinking-token latency tax). It is **multi-hosted on OpenRouter at very different speeds**, so the
> request sets `provider: { sort: "throughput" }` to land on a fast host (Together/Makora ~90–100 t/s)
> and avoid a slow route reintroducing a GLM-style geography tax. Its one tradeoff is decode speed
> (~62–100 t/s vs a Gemini flash EU edge's ~150–200+), felt mainly on long prompts — hence the
> fast Google-EU-edge fallback. The per-attempt timeout is **12s** so a slow/stalled model drops
> through quickly instead of hanging.
>
> *History:* the chain was previously Gemini-flash-lite-first (latency-optimized) with `z-ai/glm-4.7-flash`
> as a last resort; GLM measured **~15.8s** from the EU and its slug was later retired. The pivot to an
> M3 primary followed the completeness upgrade — fidelity became the priority over the last second of latency.

### Why these models (and not a "reasoning" model)

Turning rambling dictation into a structured prompt is **instruction-following, not
problem-solving**. A reasoning model (GLM 5.2, DeepSeek V4 Pro, R1, etc.) spends seconds emitting
hidden "thinking" tokens before answering — that directly lengthens the *"Refining prompt…"* wait
for marginal quality gain on a rewrite. So the chain deliberately uses **fast, non-reasoning
"Flash" instruct models**.

Cost is a non-issue at this volume: a refinement call is ~700 tokens, so even the priciest option
here is a fraction of a cent per prompt. **Latency, not cost, is the deciding factor** — hence the
Flash tier.

To change the chain, edit `Models[]` in `Talkty.App/Services/PromptRefinementService.cs`. Verify
any new slug against <https://openrouter.ai/models> first (a wrong slug just gets skipped as a
failure and falls through).

---

## The system prompt (how the agent is "trained")

The refinement quality lives in the system prompt in `PromptRefinementService.SystemPrompt`. It is
engineered from current coding-agent prompt-engineering practice. Keep this section and the code
in sync.

**Lose nothing — completeness is the #1 job** (the biggest quality lever, and the one most prone to
regress). Dictation rambles, but the small concrete details buried in it — exact file/function names,
values, ordering, preferences, edge cases, and throwaway asides ("oh, and make sure it still works on
mobile") — are the whole point of the request. The model **reformats** speech into a prompt; it never
**summarizes** it. Anything the developer said that carries an instruction, requirement, constraint,
preference, example, value, or caveat must survive into the output. It may strip only pure speech noise
(disfluencies, repetitions, abandoned thinking-out-loud) and resolve self-corrections to the final
intent ("make it red, no wait, blue" → blue). When unsure whether a phrase is noise or substance, it
keeps it. There is **no word cap** — length tracks the input: a one-liner stays one line, a detailed
ramble becomes a long, thorough prompt. This was added after omission ("it loses the small things I
mentioned") was the top quality complaint.

**Scale the structure — not the content — to the request size**: a trivial one-liner (rename, log line,
typo) becomes a single direct imperative sentence — *no* headings. Only substantial, multi-file, or
non-obvious requests get the full structure. Never pad a small ask into a big template. Scaling controls
how much *scaffolding* is added, never how much of the developer's content is kept. (Anthropic:
*"if you could describe the diff in one sentence, skip the plan."*)

**Output structure** (substantial requests) — markdown sections, only when the input supports them:

- **Task** — the goal in one or two imperative sentences.
- **Context** — stack, specific files/paths/components/functions, and existing patterns to follow.
- **Requirements** — every distinct thing asked for, one bullet per detail (never two asks merged into
  one vague bullet). This is where the small details live.
- **Constraints** — what must NOT change, libraries not to add, scope not to creep into.
- **Notes** — any detail/preference/aside that matters but fits no section above; the catch-all that
  stops small-but-important asides from being dropped.
- **Verify** — how the agent proves it worked (named test, build, expected behavior).

**Adapts to the request *type*:**

- **Bug fix** → symptom + likely location → *write a failing test that reproduces it, then fix the
  **root cause** (don't suppress), confirm it passes.*
- **Feature** → multi-file/uncertain → *explore + propose a plan before implementing*; small/clear →
  *implement directly.*
- **Refactor** → tight scope, behavior unchanged, isolated from feature/bug changes.
- **Question** → a clear question pointing at files, no implementation scaffolding.

**Principles encoded in the prompt:**

1. **Faithful AND complete.** Add nothing the speaker didn't say (no features, libraries, dependencies,
   or scope); drop nothing they did. On genuine ambiguity, emit a `> Clarify: …` note instead of
   guessing *or* silently dropping the detail.
2. **Preserve technical identifiers verbatim** — file names, paths, function/variable names, tools.
3. **Prefer existing patterns** — follow codebase conventions, reuse existing utilities over new ones.
4. **Avoid over-engineering** — no speculative abstraction, defensive code, or tests for impossible
   cases; minimal, focused diffs. (Trims the *model's* additions only — never the developer's own asks.)
5. **Be direct** — imperative voice, no filler. Length follows the input; **no word cap** (small asks
   stay short, detailed asks run long — never compress to hit a count).
6. **Clean, paste-ready output** — only the prompt text; no preamble, quotes, or code fences.

### Research basis

These choices follow Anthropic's own Claude Code guidance and current coding-agent practice:

- **Scale to size** — "if you could describe the diff in one sentence, skip the plan"; small clear
  fixes are done directly, planning is for multi-file/uncertain work.
- **Give the agent a way to verify** — a check it can run (test, build, screenshot). "Without a
  check, 'looks done' is the only signal." Fix the **root cause**, don't suppress symptoms.
- **Specific context** — reference exact files/paths, point to existing patterns to follow, describe
  the symptom + likely location for bugs.
- **Explore → plan → implement → verify** for non-trivial work; separate refactors from features.
- **Avoid over-engineering** — chasing every possible gap leads to needless abstraction, defensive
  code, and tests for cases that can't happen.
- Directness, but **no length cap** — completeness wins over brevity; the prompt is as long as the
  dictation's detail requires (a word target was found to cause the model to drop real details).

Sources: [Anthropic — Claude Code best practices](https://code.claude.com/docs/en/best-practices),
[Anthropic — prompt engineering](https://platform.claude.com/docs/en/docs/build-with-claude/prompt-engineering/overview),
prompt-engineering practice for coding agents (2026).

---

## Latency & cost

- Refinement adds **one LLM call (~1–2s)** on top of transcription. This is expected — Prompting is
  a deliberate "make this a proper prompt" action, not the instant-dictation path. Leave Prompting
  off for fast plain dictation.
- Per-call cost is a fraction of a cent. The per-attempt timeout is
  `Constants.PromptRefinementTimeoutMs` (12s) — tight enough that a slow model drops through.

---

## File map

| Concern | File |
|--------|------|
| Refinement service + model chain + system prompt | `Talkty.App/Services/PromptRefinementService.cs` |
| Interface | `Talkty.App/Services/IPromptRefinementService.cs` |
| Overlay toggle state | `Talkty.App/ViewModels/OverlayViewModel.cs` (`IsPromptMode`) |
| Overlay icon (hover-revealed sparkle) | `Talkty.App/Views/OverlayWindow.xaml` (`PromptButton`) |
| Toggle → pipeline wiring | `Talkty.App/MainWindow.xaml.cs` (`OnOverlayPromptModeChanged`) |
| Pipeline branch (refine before clipboard) | `Talkty.App/ViewModels/MainViewModel.cs` (`PromptMode`) |
| Timeout constant | `Talkty.App/Constants.cs` (`PromptRefinementTimeoutMs`) |
