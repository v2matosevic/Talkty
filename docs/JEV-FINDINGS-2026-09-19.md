# Jev: what a closer look changed

Research date: 19 September 2026, after the [prompt fidelity check](PROMPT-FIDELITY.md) shipped
locally. Prior art: Athena `docs/jev/PLAYBOOK.md` and `docs/ade/jev-system-one-research-2026-09-18.md`.
This document records only what is **new or corrected** since those, plus what it means for Talkty.

One paid probe was made (683 input tokens, **$0.0000287**). Everything else is documentation reading
and schema inspection.

---

## 1. Two facts in the earlier research are now wrong

**The OpenRouter route accepts structured JSON instructions and criteria.** The 18 September
research concluded "the gateway schema currently limits question instructions to strings and Choice
descriptions to strings". The current OpenAPI says otherwise for every field, and a live request
confirmed it: HTTP 200, structured objects accepted for `instructions`, Choice option descriptions,
and Score level descriptions.

```text
DecisionsChoiceQuestion.criteria.*   anyOf: string | object | array | null
DecisionsChoiceQuestion.instructions anyOf: string | object | array
DecisionsNoulQuestion.criteria.true/false, DecisionsScoreQuestion.criteria[]  same
```

**Score works on the gateway.** `DecisionsScoreQuestion` and `DecisionsScoreAnswer` are both in the
schema, and the live probe returned a score, a per-level probability distribution, a confidence, and
a `legend` echoing the structured levels back. The earlier work recorded Score as covered only by
offline response-validation tests, never live. It is now live-qualified on our route.

Two smaller ones: `session_id` exists for grouping related requests (never sent to the provider,
used for observability), and the gateway makes Choice `probabilities`/`confidence` optional while
the direct contract requires them. Our client already rejects a response missing them, which stays
the right call.

Confidence: **high**, this is an inspected schema plus one observed 200 response.

---

## 2. The non-determinism I reported was real but mischaracterised

I reported that the model layer "changed its mind" between runs, with `hold-10` going from one
concern to zero. TypeSafe's own [self-consistency cookbook](https://docs.typesafe.ai/cookbooks/consistency_noul_cookbook)
explains what is actually happening: across 15 repeats of a 14-question rubric, Jev's mean
per-question probability standard deviation was **0.0102**, lower than every LLM condition they
tested including temperature 0. But their `covered` answers spanned 0.43 to 0.53, **crossing a 0.5
decision threshold**.

So the probabilities barely move. What moves is the decision, when the true value happens to sit on
top of my threshold. That is a property of putting a hard gate at 0.80, not evidence that Jev is
flaky. My earlier wording overstated it and the record now says so.

The cookbook's own answer is not redundancy but an explicit **uncertainty band**: map 0.30–0.70 to
an `uncertain` outcome routed to human review, keeping the underlying probability visible. Our
equivalent is a third state between "tell the user" and "silence": record it as uncertain, surface
nothing, and use the accumulated band to tune later.

---

## 3. The measured comparison that matters

The probe sent the same dictation and rewrite two ways. The dictation renames `getUserById` and says
"do not deploy this to production"; the rewrite changes the identifier to `getUserByID` and drops
the prohibition.

| Question shape | Answer | Confidence | Would it reach the user? |
|---|---|---|---|
| Choice: preserved / omitted / contradicted / unclear | `preserved` at p=0.50 | **0.33** | **No.** Below both thresholds, silently dropped |
| Score: 3 ordered levels of "how much survives" | **0.91** (level 1, "a load-bearing detail is missing or changed"), 0.87 of the mass on that level | **0.80** | Yes, clearly |

On this case the Score was confident and right where the Choice was an unsure shrug. That is a
direct argument that **Score is the better primitive for a "how much survived" judgment**, which is
a magnitude question, not a category question. I had not used Score at all.

Honest limits: this is **one probe**. It also collapsed a two-sentence dictation into a single
Choice, where the shipped implementation splits clauses and asks per clause, so it is not a clean
A/B. It justifies running the corpus both ways; it does not by itself prove Score wins.

---

## 4. The bigger miss: I put the model in the least valuable position

Jev's documented role, in TypeSafe's own [intent routing](https://docs.typesafe.ai/patterns/intent-routing)
pattern, is a fast cheap classifier **in front of** expensive work, so the expensive resource is
only invoked when it is actually needed. I put it **behind** the expensive work, as an auditor.

Auditing is a legitimate use and it caught real losses. But look at what Talkty does today. Every
Prompting dictation gets the same treatment: one ~1,100-token system prompt, one LLM call, one to
three seconds, whether you said "add a loading spinner to the export button" or dictated four
hundred words of multi-part feature work. The refinement model is asked, inside that system prompt,
to infer the request type and to scale its own structure.

A single Jev call **before** refinement, at roughly 500 ms and $0.00006, could decide:

- **Does this need expanding at all?** A one-line ask does not. Skip the LLM entirely and hand over
  the cleaned transcription. That is faster and cheaper than today, not slower.
- **What kind of request is it** (bug / feature / refactor / question)? Code then selects a short
  purpose-built system prompt instead of one long general one that has to cover every case.
- **How complex is it?** Score it, and let code choose the fast model or the quality model, instead
  of always starting with the fast one and paying an escalation when the completeness guard trips.

This inverts the economics. Today Prompting always costs a generation call. With a classifier in
front, the trivial cases cost nothing, and the genuinely complex ones get the better model on the
first attempt rather than the second.

Confidence that this is the better shape: **moderate**. Confidence that it would measurably improve
prompts: **unknown** until it is built and compared, which is the same standard the fidelity check
is being held to.

---

## 4b. What happened when it was built

Section 4's proposal is now implemented and live-qualified: see
[PROMPT-PLANNING.md](PROMPT-PLANNING.md). 18/18 on the labeled corpus including the holdout split,
zero false skips, median 511 ms, $0.0008 for the whole corpus.

The interesting part was a mistake it exposed. I gated the skip decision on the complexity rubric's
confidence, copied from a sibling profile. Measured, that confidence does not discriminate in this
workload at all (0.35-0.86 for already-a-prompt against 0.25-0.99 for needs-organising, fully
overlapping) and cost five of eight correct skips while preventing none of the dangerous errors.
The two question signals separate cleanly on their own. This is the playbook's "thresholds must be
evaluated per question and workload" as a lived lesson rather than a quoted one.

## 5. Concrete changes to the shipped check

In rough order of value, none of them yet made:

1. **Restructure the criteria as JSON** with `what` / `not_for` / `examples` per option, instead of
   the current prose strings. The docs state this sharpens option boundaries, and it should also cut
   tokens versus the long sentences we send now.
2. **Add an uncertainty band.** Record 0.30–0.70 explicitly as uncertain rather than folding it into
   silence, so there is data to tune against later.
3. **Try Score against the per-clause Choices on the corpus.** Cheap to run, and section 3 suggests
   it may be both better and smaller.
4. **Per-kind thresholds.** A missing identifier is cheap to show and nearly always right; an added
   requirement is the fuzziest judgment we ask for. One threshold pair for both is wrong, and
   TypeSafe's [confidence-gated routing](https://docs.typesafe.ai/patterns/confidence-routing)
   pattern is explicitly about setting the bar by the consequence of being wrong.
5. **Drop the `attention` question.** It costs tokens on every request and only reorders results the
   per-clause answers already provide.
6. **Send `session_id`** so one dictation's calls group together in observability.

Changing the question profile invalidates the threshold qualification, so 1 through 5 should land
together with one re-run of the corpus, not piecemeal.

---

## 6. What the community is actually doing

The [awesome-jev directory](https://github.com/hellogumbo/awesome-jev) listed **488 entries** on
18 September. The overwhelming majority are language SDKs, which is noise for us. The recurring
application patterns worth naming:

- **Rerank and route** rather than generate: `llama-index-jev` is a reranker plus router, billed as
  cheaper than LLM-as-judge. This matches the intent-routing pattern above.
- **Validate meaning next to validating shape**: `zod-jev` frames it as "Zod validates the shape,
  Jev validates the meaning". That is the same two-layer split as our exact checks plus judgment,
  arrived at independently, which is mild evidence the split is the natural one.
- **Guardrails in front of an LLM**: an agentgateway example runs Jev as a prompt guardrail inside a
  proxy. Again in front, not behind.
- There are community **.NET SDKs** (`typesafe-dotnet-sdk`, `TypeSafeAI.Net`). They target the
  direct TypeSafe API, not the OpenRouter route, so they do not replace our client, which is
  deliberately dependency-light and already qualified against the gateway.
- TypeSafe publishes an **official agent skill** for Claude Code and compatible agents, covering
  primitives and how to structure evaluations. Worth installing before the next Jev build.

There are also **cookbooks** the earlier research never opened, covering parallel questions,
reranking, guardrails, citation checks, extraction and hierarchical classification, plus the
self-consistency pair used in section 2.

---

## Evidence

Live probe response: `docs/evidence/jev-fidelity-2026-09-19/capability-probe.json`. Schema
inspection was done against `https://openrouter.ai/openapi.json` on 19 September 2026; the route and
schema are alpha and can change, so re-check before relying on structured criteria in a release.
