# Prompt fidelity check — did the rewrite keep what you actually said?

Implemented 19 September 2026. Tracking issue **Athena-kukk**. Brief:
`B:/Coding/Athena/docs/jev/handoffs/TALKTY.md`, playbook section 6C.

**State: implemented and locally verified on Windows. Not released, not installed.** Source commit
`74f974d`. The public 1.3.5 installer does not contain it. The separate `talkty-mac` app does not have this feature and
nothing here should be read as Mac parity.

---

## The problem this solves

[Prompting](PROMPTING.md) rewrites a dictation into a structured coding-agent prompt. The existing
safety net against a lossy rewrite is `PromptRefinementService.IsSuspectedSummary`: if a substantial
dictation comes back under 60% of its own length, the model summarised and the chain escalates.

That guard measures **length**. A fluent rewrite that keeps the word count while quietly dropping
"do not deploy this to production", or turning `getUserById` into `getUserByID`, or changing 45
seconds to 60, sails straight through it. On a 24-case labeled corpus the length guard caught
**0 of 13** deliberate fidelity losses — not because it is broken, but because none of them are
length problems. It stays exactly as it is; this check is additive.

---

## How it works

```
dictation ─▶ transcribe ─▶ TextPostProcessor ─▶ PromptRefinementService (Prompting)
                                                        │
                                                        ├─▶ clipboard / auto-paste   (unchanged, never delayed)
                                                        │
                                                        └─▶ PromptFidelityService    (fire-and-forget)
                                                              ├ 1. PromptFidelityAnalyzer  — exact, local, free
                                                              └ 2. JevDecisionClient       — bounded judgment, ~$0.00006
```

### Layer 1: exact checks, on this PC

`PromptFidelityAnalyzer` decides everything a string comparison can decide. Each finding is
literally true, costs nothing, and does not depend on a model being right:

| Check | Catches | Deliberately does not fire when |
|---|---|---|
| Code identifiers | A file, path, function or dotted name from the dictation that is absent from the prompt, compared **case-sensitively** so a one-character drift shows up | The token is a bare acronym (API, SQL, CSS) — dropping a redundant mention is harmless |
| Numeric literals | A stated value missing from the prompt. `30` is satisfied by `30s`, `30 ms` or `30-second`, but not by `300`, `1.30` or `v30` | The digits are glued to a word (`v2`, `h1`), or the speaker **superseded** the value ("300, no wait, make it 600") |
| Prohibitions | The dictation contained a "do not" (English or Croatian: `nemoj`, `ne smij…`, `nikako`, `zabranjeno`, …) and the prompt contains **none at all** | Only *some* prohibitions were consolidated — a count check cannot tell consolidation from loss, and a false alarm on faithful editing is the worse failure |

### Layer 2: the bounded judgment

What survives layer 1 is genuinely semantic: "is this whole instruction still in there, in some other
wording?" That goes to **TypeSafe Jev** (`typesafe/jev-1.13`) through the OpenRouter connection
Prompting already uses. Jev is a decision model — it selects from options the caller supplies. It
cannot write a replacement prompt, an explanation, or any free text, and this integration gives it
no other answer space.

One request carries one shared state and all questions (fan-out — they are independent, so repeating
the transcript per question would only waste input tokens):

- **state**: `{ dictation: [{id: "c1", text: …}, …], prompt: "…" }`. Text only. No audio, no
  settings, no key, no machine or user identity.
- **one Choice per source clause** (at most `Constants.JevMaxClauses` = 12; extra sentences are
  merged, never truncated, so no part of the dictation goes unasked): `preserved` / `omitted` /
  `contradicted` / `unclear`. `preserved` explicitly covers a clause that carries no instruction at
  all, so removing filler is not an omission.
- **one Noul**: does the prompt state a requirement the dictation never contained?
- **one Choice over the clause IDs plus `none`**: which clause is most at risk. Used only to
  **order** concerns — it never creates one, because a single Choice among clauses always has to
  pick something.

`JevDecisionClient` then refuses to half-trust the answer. An unknown option, a distribution that
does not sum to 1, a winning option that is not the maximum, a missing confidence, an unexpected
model build or missing usage all return `Invalid` rather than a value. Missing cost stays **unknown**
— never recorded as zero.

### Route facts

```text
POST https://openrouter.ai/api/alpha/decisions
requested model: typesafe/jev-1.13
returned model:  typesafe/jev-1.13-20260917   (allow-listed exactly; a future alias is refused)
body:  {model, state, questions, provider:{data_collection:"deny", zdr:true, allow_fallbacks:false}}
```

Not chat completions. **Not** `/api/v1/api/alpha/decisions` — OpenRouter's generated OpenAPI
server/path pair composes that URL and it 404s. `JevDecisionClientTests` asserts the endpoint
constant so nobody "corrects" it back to the documentation.

Accepting `data_collection: deny` and `zdr: true` is the strictest posture the route offers. It is
not an independent audit of the provider's retention practice.

---

## What it will not do

- Change the delivered text. The prompt reaches the clipboard exactly as the refiner wrote it.
- Delay delivery. The check starts **after** the prompt is on its way to the clipboard and returns
  through a toast if it has something to say.
- Touch local transcription. Local speech stays fully offline and unchanged.
- Send audio. Only the dictation text and the generated prompt leave the machine.
- Add a paid retry chain, switch the refinement model, or reach for a stronger model. One attempt,
  four seconds, then the established flow stands.
- Claim the prompt is wrong. A concern shows the user's **own words** and a **fixed label chosen by
  code**; the model contributes a label and a clause ID, never prose.

A failed, uncertain or unaffordable judgment is silent. Off, Record only and Review are distinct in
the record, as are `Evaluated`, `CodeOnly`, `Skipped`, `Cancelled` and every transport failure code.

---

## Modes

Settings ▸ Cloud & Prompting ▸ **Prompt check**.

| Mode | Behaviour |
|---|---|
| **Record only** (default) | Runs the check and keeps a local comparison record. Shows nothing, changes nothing. |
| Show concerns | As above, plus a toast after delivery when a concern clears the thresholds. |
| Off | No check, no request. |

The check only ever runs when **Prompting produced a prompt** — that is, when the user has already
opted into cloud processing with their own OpenRouter key and switched Prompting on for that
recording. Plain dictation never triggers it.

> **Decision for Marko.** The brief specifies "start in Record only", so the shipped default is
> `RecordOnly`. That means a Prompting user gets one extra Jev request per generated prompt
> (about $0.00006) without separately choosing it, disclosed in the settings panel. If you would
> rather nobody spends anything they did not tick, change `AppSettings.PromptFidelity`'s default to
> `Off`; nothing else needs to move.

---

## Bounds and budget

All in `Constants.cs`, all deliberately smaller than the route would allow.

| Control | Value |
|---|---|
| Deadline per request | 4 s (`JevDecisionTimeoutMs`) |
| Request / response ceiling | 32,000 / 128,000 bytes |
| Source clauses | 12 (sentences beyond that are merged, not dropped) |
| Skipped above | 6,000 dictation chars / 12,000 prompt chars — code layer still runs |
| Reservation per attempt | $0.002, replaced by the reported cost; an unknown cost keeps the reservation |
| Local daily allocation | $0.25 per calendar day |
| Rate limit | 20 attempts/minute; an identical pair is not re-evaluated within 60 s |
| Concerns shown at once | 2 |

The ledger lives in `%AppData%/Talkty/jev-fidelity.json`. It holds SHA-256 hashes, lengths, concern
kinds, clause IDs, status, timing and usage — **no dictation or prompt text**, and no credentials.
It is a local reservation limit at the researched price, not an OpenRouter account credit limit and
no protection against a provider price change.

### Thresholds (provisional)

`JevFidelityMinProbability` 0.80, `JevFidelityMinConfidence` 0.70, `JevFidelityMinAddedProbability`
0.85 (higher, because Noul reports no confidence statistic to corroborate it). Chosen before the
live run to favour precision and **not changed afterwards**; the holdout split was never used to
select them. A Jev confidence is a distribution statistic, not a measured probability that the
answer is correct.

---

## The corpus

`tools/jev-fidelity-corpus.json` — 24 cases, written and labeled **before** any live call.
14 development, 10 holdout; English, Croatian and one mixed-language case. It covers every scenario
the brief names: a long fluent rewrite dropping "do not deploy", a one-character identifier change,
changed values, final self-corrections, faithful short and long rewrites, harmless filler removal,
multiple independent requirements (kept and one-dropped), contradictions, and an invented
requirement. 13 cases should raise a concern; 11 must stay silent, so neither "always alarm" nor
"never alarm" can score well.

One label was corrected after the first run: **hold-09** was marked model-only, but the speaker's
"I do not want to break them" is that dictation's only prohibition and the rewrite has none, so the
exact layer catches it too. The label was wrong, not the code.

---

## Verification

### Local, Windows, 19 September 2026

| Check | Result |
|---|---|
| `dotnet build` (Release) | 0 warnings, 0 errors |
| `dotnet test` (Release) | **253 passed**, 0 failed (was 143 before this work) |
| Settings UI, off-screen render | Prompt check panel renders in the dark theme; picker defaults to Record only |

Coverage by area, because "tests pass" says nothing about what they touch:

| Area | Where | What it pins down |
|---|---|---|
| Request contract and response validation | `JevDecisionClientTests` | The pinned route, model and privacy block; every rejection - unknown option, bad distribution, non-maximum winner, missing confidence, wrong model, both token spellings, conflicting spellings, missing cost |
| **Transport failure paths** | `JevTransportTests` | Against a real local socket: 401/402/429/500/503, oversized body, a provider that hangs past the deadline, a dropped connection, garbled JSON, an unreachable host, and caller cancellation as distinct from a timeout. Also asserts what goes out **on the wire** - bearer header, pinned provider block, key never in the body |
| **Orchestration** | `PromptFidelityServiceTests` | Mode and key gating, size bounds, duplicate suppression, reservation vs reported cost, status mapping for every failure kind, what lands in the record, and that Record only finds concerns but never raises one |
| Exact layer | `PromptFidelityAnalyzerTests` | Clause numbering and merging, token extraction, self-correction, unit tolerance, prohibition counting, silence on faithful rewrites |
| Thresholds | `PromptFidelityPolicyTests` | Every gate, ordering, the cap, and that the attention question alone never creates a concern |
| Budget ledger | `JevFidelityLedgerTests` | Cap boundary on both sides, outstanding reservations counting toward it, day rollover, duplicate expiry, corrupt file, retention trim |
| Pipeline wiring | `PromptFidelityFlowTests` | Through the real view model: what the check is handed, that delivery is untouched, that plain dictation and failed refinement are never checked, the concern toast, settings persistence, ESC and new-recording cancellation |

The corpus test prints the baseline comparison table on every run
(`PromptFidelityCorpusTests.ExactLayer_BeatsTheLengthGuardWithoutAddingFalseAlarms`), so the claim
below is a test result rather than a sentence in this file.

### Two defects the later passes found

**The concern would have been cut in half where it matters most.** While Talkty sits in the tray —
the normal case, because you are dictating into another app — `MainWindow.OnRequestShowToast`
routes a Warning toast to a Windows tray balloon, and the shell truncates `NOTIFYICONDATA.szInfo`
at 256 characters without telling anyone. The worst realistic two-concern message measured **326
characters**. It had been asserted for content through the view model and never looked at. The
message is now bounded (`Constants.FidelityToastMaxChars` = 240, with a per-concern quote budget),
quotes break on a word boundary rather than mid-word, and three tests pin it. Rendered evidence:
[one concern](evidence/jev-fidelity-2026-09-19/ui/toast-one-concern.png),
[two concerns](evidence/jev-fidelity-2026-09-19/ui/toast-two-concerns.png).

### A bug the second pass found

Writing the ledger tests properly turned up a real defect. `Settle` did not roll the local day, so a
reservation taken at 23:59:59 and settled at 00:00:01 wrote its charge onto a day that the next read
immediately zeroed - the charge vanished. Fixed by rolling the day before settling, which attributes
the charge to the live day instead. It now over-counts by one reservation across that boundary
rather than losing a charge.

The test that should have caught this earlier could not. The original daily-cap test asserted
`refusals > 0 || JevMaxAttemptsPerMinute < possible`, and the right-hand side is always true
(20 < 125), so it passed no matter what the ledger did. The cap is unreachable through `TryReserve`
alone because the per-minute limit bites first, so the replacement seeds the ledger file directly
and checks both sides of the boundary.

### Live qualification (paid)

`pwsh tools/jev-fidelity-check.ps1 -Run` — announced up to $0.048 against a **$0.05 ceiling**, using
the OpenRouter key already saved in `settings.json` (decrypted through the app's own
`ApiKeyProtector`, never printed or written). Synthetic corpus text only. **Run three times**; all
three produced identical verdicts on all 24 cases.

| | Result |
|---|---|
| Attempts / evaluated | 24 / 24 per run, **0 transport failures** across 72 requests |
| Returned model | `typesafe/jev-1.13-20260917` |
| Fidelity-loss cases caught | **13 of 13** in every run — exact layer 9, model layer 10–11 |
| Existing length guard, same cases | **0 of 13** |
| False alarms on 11 faithful cases | **0** in every run, from either layer |
| Clause attribution (run 03, asserted by the script) | **12 of 12** correct; model layer **9 of 9** |
| Evaluation time | run 01 median 493 ms, run 02 median **484 ms**, run 03 median 928 ms (max 2,685 ms) |
| Input tokens | 31,923 per run; 828 / 1,195 / 2,197 min / median / max |
| Reported cost | **$0.001340766** per run; $0.001352 allocated of the $0.05 ceiling |
| Attempts with unknown cost | 0 |

Total spend across all three runs: about **$0.004** of the $0.05 ceiling.

Per-case evidence: [run 01](evidence/jev-fidelity-2026-09-19/results.json),
[run 02](evidence/jev-fidelity-2026-09-19/run-02/results.json),
[run 03](evidence/jev-fidelity-2026-09-19/run-03/results.json). All three are retained. Run 01's
per-case data is correct but its **summary block** under-reports token and cost totals — a
PowerShell aggregation bug (`Measure-Object` cannot read keys off an `OrderedDictionary`) fixed
before run 02. Keeping it is the point: the failure is part of the record.

### The model layer is not deterministic, and the redundancy earns its keep

Run 03 added an automatic attribution check, because until then I had confirmed "the concern points
at the right sentence" by reading the evidence file myself, which is not verification. The script
now asserts it and prints the count.

Re-running also exposed something a single run hid. Between run 02 and run 03 the **model layer
changed its mind on two cases**: `dev-04` went from 2 model concerns to 1, and `hold-10` from 1 to
**0**. Every per-case verdict stayed correct, but on `hold-10` that was only because the exact layer
caught it anyway — the model layer alone would have missed a real loss in run 03. Treat "model layer
10–11 of 13" as the honest range rather than 11, and treat the two layers as genuinely
complementary rather than one being a superset of the other.

Latency is likewise not a fixed property: the same 24 requests ran at a 484 ms median one minute and
a 928 ms median another. That costs the user nothing here because the check runs after delivery, but
it would matter immediately to any future design that put it in front of the clipboard.

UI evidence: [prompt check panel](evidence/jev-fidelity-2026-09-19/ui/settings-prompt-check.png),
[page in context](evidence/jev-fidelity-2026-09-19/ui/settings-cloud-prompting.png),
[one concern](evidence/jev-fidelity-2026-09-19/ui/toast-one-concern.png),
[two concerns](evidence/jev-fidelity-2026-09-19/ui/toast-two-concerns.png), and the harnesses that
produced them.

### What these numbers do not establish

Twenty-four agent-authored cases cannot establish a production error rate, calibrated confidence, or
broad Croatian accuracy — three runs agreeing with each other is consistency, not correctness, and
run 03 showed the model layer is not even fully consistent with itself. The four Croatian cases
passed; that is four cases. Nothing here has been measured against real dictation, and no user has
yet seen a concern. The timings are the HTTP evaluation only and exclude transcription, refinement
and clipboard work — which is also why they cost the user nothing: the check runs after delivery.

Still untested: nothing has run end to end through the installed app with a microphone, so no
concern has ever been raised by a real dictation. The ledger's locking is in-process only, which is
sound today because Talkty is single-instance, but would need revisiting if that ever changed. The
tray-balloon path itself is reasoned from the Windows limit and the bounded message, not observed —
the rendered evidence is the in-app toast.

On three loss cases the model reported more than one kind (for example a changed value read as both
`ContradictedClause` and `AddedRequirement`). Each is defensible on the text, and none occurred on a
faithful case, but kind attribution is looser than clause attribution: every model-only case landed
on exactly the labeled clause.

---

## Running it again

```powershell
# Plan only, sends nothing:
pwsh tools/jev-fidelity-check.ps1

# Paid pass into a fresh evidence directory (it refuses to overwrite one):
pwsh tools/jev-fidelity-check.ps1 -Run -Out docs/evidence/jev-fidelity-<date>

# Development split only, while tuning:
pwsh tools/jev-fidelity-check.ps1 -Run -Split development
```

Offline, no spend:

```powershell
dotnet test Talkty.Tests --filter PromptFidelity
```

---

## File map

| Concern | File |
|---|---|
| Transport + strict response validation | `Talkty.App/Services/JevDecisionClient.cs` |
| Exact checks, clause numbering, labels | `Talkty.App/Services/PromptFidelityAnalyzer.cs` |
| Question profile, orchestration, record | `Talkty.App/Services/PromptFidelityService.cs` |
| Thresholds → concerns | `Talkty.App/Services/PromptFidelityPolicy.cs` |
| Budget, rate limit, comparison record | `Talkty.App/Services/JevFidelityLedger.cs` |
| Interface + concern event | `Talkty.App/Services/IPromptFidelityService.cs` |
| Mode enum (persisted by number) | `Talkty.App/Models/PromptFidelityMode.cs` |
| Pipeline hook, toast, cancellation | `Talkty.App/ViewModels/MainViewModel.cs` (`StartFidelityCheck`, `OnFidelityConcernRaised`) |
| Settings picker | `Talkty.App/ViewModels/SettingsViewModel.cs`, `Talkty.App/Views/SettingsWindow.xaml` (PROMPT CHECK) |
| Bounds and thresholds | `Talkty.App/Constants.cs` |
| Labeled corpus | `tools/jev-fidelity-corpus.json` |
| Paid live qualification | `tools/jev-fidelity-check.ps1` |

## Next, if it is worth continuing

1. Run in Record only through real dictation for a while and read
   `%AppData%/Talkty/jev-fidelity.json`: how often does a concern fire, and on what?
2. Only then decide whether Show concerns earns its place, and re-check the thresholds against real
   traffic rather than fixtures.
3. Retire the profile if it adds calls and interruptions without catching anything real. A poor
   result is a valid result.
