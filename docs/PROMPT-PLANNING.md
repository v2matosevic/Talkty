# Prompt planning — decide before spending, not after

Implemented 19 September 2026, following [JEV-FINDINGS-2026-09-19.md](JEV-FINDINGS-2026-09-19.md).

**State: included in the 1.4.0 Windows package, default Off; see [delivery status](RELEASE-1.4.0.md).**
The public 1.3.5 installer does not contain it, and `talkty-mac` does not have this feature.

This is an advanced `promptPlanning` setting in `%AppData%/Talkty/settings.json`
(0 = Off, 1 = Hints, 2 = Full), not a mode picker in the current Settings UI.
Enable ordinary Prompting in Settings > Cloud & Prompting first. Close Talkty
before manually editing its settings file.

---

## What it changes

Today every Prompting dictation gets identical treatment: one ~1,100-token system prompt, one LLM
call, one to three seconds, whether you dictated four hundred words of multi-part feature work or
said "add a loading spinner to the export button". The second case does not need a model at all.

A single decision call before refinement, around half a second and six hundredths of a cent, answers
three questions at once:

| Question | Primitive | Used for |
|---|---|---|
| Would a structured prompt actually help here? | Noul | Whether to refine at all |
| Bug / feature / refactor / question / chore? | Choice | A one-line hint so the LLM stops inferring it |
| How much work is being asked for? | Score (3 levels) | Which model starts the chain |

This is the position a decision model is designed for: cheap classification in front of expensive
generation. The [fidelity check](PROMPT-FIDELITY.md) put it behind, as an auditor. Both are valid,
but only this one can make Prompting **faster** rather than slower.

---

## Modes

Settings value `PromptPlanning`, persisted by number.

| Mode | Behaviour |
|---|---|
| **Off** (default) | No classification, no request. Prompting behaves exactly as it always has. |
| Hints | Classify, and use the result only to help: request-kind hint, and substantial work starts on the higher-quality model. Every dictation is still refined, so the prompt can only improve or stay the same. |
| Full | As Hints, plus: skip the refinement model entirely when the dictation is already a usable prompt. The only mode that changes what you receive. |

Everything fails safe. No key, no answer, a slow answer, a partial answer or any transport problem
all return the default plan, which is byte-for-byte today's behaviour. The classifier can make
Prompting faster; it cannot make it fail.

---

## Live results

`pwsh tools/jev-planning-check.ps1 -Run` over `tools/jev-planning-corpus.json`: 18 cases, 11
development and 7 holdout, English and Croatian, labeled before any live call. The consequential
error is a **false skip** — the speaker asked for a prompt and would receive raw dictation.

| Run | Thresholds | Correct | False skips | Missed skips |
|---|---|---|---|---|
| 01 | blind (0.15 / 0.5 / conf ≥ 0.75) | 12/18 | **0** | 6 |
| 02 | dev-tuned (0.35 / 0.85 / conf ≥ 0.75) | 13/18 | **0** | 5 |
| 03 | confidence gate removed | **18/18** | **0** | **0** |

Final run: 18/18 including all 7 holdout cases, zero false skips, request kind correct on 14 of the
16 where it was confident (abstaining on the other 2), median 511 ms against a 1,500 ms deadline,
19,120 input tokens for **$0.00080** across the whole corpus.

### The mistake that made runs 01 and 02 bad

I gated the skip on the complexity rubric's confidence at ≥ 0.75, copied from a sibling profile
without checking whether it discriminates here. Measured on the development split it does not:

```
already-a-prompt cases   confidence 0.35 – 0.86
needs-organising cases   confidence 0.25 – 0.99      fully overlapping
```

One clearly multi-part refactor sat at 0.25 while trivial one-liners sat at 0.86. The gate cost five
of eight correct skips and prevented none of the dangerous errors. It is removed.

What does separate, cleanly, is the two signals themselves:

```
needs_structure   0.08 – 0.19  (already a prompt)   vs   0.60 – 0.86  (needs organising)
complexity        0.09 – 0.81                       vs   0.97 – 1.98
```

Both must agree before anything is skipped. That agreement is the safety, not a confidence number.
The playbook warned exactly this — "thresholds must be evaluated per question and workload" — and I
imported one instead of evaluating it.

Thresholds were chosen from the **development split only**; the 7 holdout cases were never used to
pick them and were correct on the first run that used them.

---

## Honest limits

Eighteen agent-authored cases. That establishes no production error rate, and four Croatian cases
are four Croatian cases. Nothing has run against real dictation.

More importantly: **zero false skips on ten should-refine cases is weak evidence of safety.** The
corpus contains the failure mode I thought of (a short sentence hiding several requirements) but
real speech will contain ones I did not. A false skip is quiet — you get your own words back, which
looks like Prompting simply did little — so it will not announce itself. That is the argument for
running in Hints before Full.

**The thresholds are tied to this corpus's composition, not just its labels.** A peer hit exactly
this on the same day: they set a gate where their two classes separated cleanly with an empty band,
then fixed the retrieval feeding it, and the classes overlapped — the separation had been an
artefact of a worse input distribution, not a property of the question. My 0.35 and 0.85 were
measured on eighteen cases I wrote. If the refinement pipeline, the transcription quality or the
kind of thing you dictate changes, they need re-measuring rather than trusting.

Unmeasured: whether the request-kind hint actually improves the generated prompt. It removes
inference work from the model, which is a reason to expect improvement, not evidence of it. Same for
starting substantial work on the quality model: it avoids a known escalation, but no prompt-quality
comparison has been run.

---

## Bounds

| Control | Value |
|---|---|
| Deadline | 1,500 ms (`JevClassifierTimeoutMs`) — the speaker is waiting, unlike the fidelity check |
| Skip requires | `needs_structure` ≤ 0.35 **and** complexity ≤ 0.85, both present |
| Kind used as a hint only when | probability ≥ 0.70 and confidence ≥ 0.60 |
| Quality model from | complexity ≥ 1.5 |
| Skipped above | 6,000 dictation chars |

---

## Running it

```powershell
pwsh tools/jev-planning-check.ps1                    # plan only, sends nothing
pwsh tools/jev-planning-check.ps1 -Run               # paid, ~$0.0008 for the corpus
pwsh tools/jev-planning-check.ps1 -Run -Split development
dotnet test Talkty.Tests --filter PromptClassifier   # offline, free
```

## File map

| Concern | File |
|---|---|
| Questions, thresholds, plan | `Talkty.App/Services/PromptClassifier.cs` |
| Mode enum (persisted by number) | `Talkty.App/Models/PromptPlanning.cs` |
| Pipeline hook | `Talkty.App/ViewModels/MainViewModel.cs` (`PlanRefinementAsync`) |
| Hint and model steering | `Talkty.App/Services/PromptRefinementService.cs` (`BuildMessages`, `BuildChain`) |
| Corpus and live check | `tools/jev-planning-corpus.json`, `tools/jev-planning-check.ps1` |

## Next

Run in Hints for a while over real dictation. Hints cannot lose a prompt, so it is the honest way to
find out whether the kind classification is any good before letting the classifier skip anything.
