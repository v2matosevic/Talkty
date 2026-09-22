# Full-context transcription latency, 22 September 2026

## Scope and decision

Marko requested syncing six local-only commits and improving the time between finishing
speech and receiving text, for local and cloud recognition. When asked about the tradeoff,
he chose: **"Preserve full-recording context and accuracy, even if the speedup is smaller."**

The six commits through `2ba0572` were pushed to `origin/main`. Their GitHub CI passed:
[run 35753822017](https://github.com/v2matosevic/Talkty/actions/runs/35753822017).

This change implements recognition during pauses, CUDA Flash Attention, an existing MAI
request correction, and removal of a blocking command-preview wait. It does not split
recordings into phrases, change the selected models, lower audio quality, bypass prompt
completeness checks, or paste provisional text.

## What changed

- `SpeculativeTranscriber` starts the same whole-context recognition during a quiet pause:
  400 ms locally, 800 ms in cloud mode. It polls only a small tail snapshot every 150 ms.
  It retains at most one candidate/result at a time, with no more than three local early
  passes or one cloud early pass per recording. Slow local passes back off. Each early
  pass has a five-second cancellation deadline. Early cloud calls never retry or use the
  backup provider; normal final-pass recovery still applies.
- After the actual microphone flush, the candidate is reused **only when its entire
  trimmed float sample array equals the final array**. Changed audio, including speech in
  the last driver buffer, causes a full final pass. Early failure or empty text also falls
  back to the normal pass. Settings changes invalidate the candidate. No phrase joining,
  suffix guessing, word deduplication, or confidence-based acceptance is involved.
- Silence trimming uses the existing RMS threshold, 100 ms windows and 200 ms margins.
  Both scans are now anchored to the recording start. The previous backwards scan moved
  its window boundaries whenever a new buffer arrived. Stable windows make identical
  spoken audio reusable after additional silence. The partial last frame is examined;
  the recording is retained if no speech window was found.
- `TranscriptionService` serializes decoder access and model switches. A cancelled early
  decode must unwind before another enters the same native state. Shutdown frees the
  engine after active work without blocking the UI dispatcher.
- The existing Alt+W preview previously called `Wait(3 seconds)` on the dispatcher even
  though its cancelled async delay could require that dispatcher to resume. It now runs
  off the dispatcher and stops without that blocking wait. Commands retain their separate
  preview and never use the ordinary dictation candidate.
- CUDA enables `WhisperFactoryOptions.UseFlashAttention`. Whisper.net 1.9.0 defaults it
  off. The model, context size, vocabulary and sampling settings remain the same. CPU
  and Vulkan attention configuration is unchanged.
- `TranscribeDuringPauses` defaults on, persists through Settings save/apply, and can be
  disabled under Behavior. The tooltip discloses that cloud can bill one extra pass,
  including cancelled recordings. Selecting local recognition still keeps audio local.
- Logs now record stop-to-clipboard and stop-to-paste duration, plus early-result reuse.
  These timings include the relevant application work; the benchmark below does not
  operate the user's clipboard or desktop.

## Cloud defect found and fixed

MAI returned `HTTP 400 {"error":{"message":"Provider returned 400","code":400}}`
with both this branch and the installed `B:\Talkty\Talkty.App.dll` dated September 20.
The installed control used the original untrimmed fixture and no vocabulary hints.

Isolated controls established:

| Request | Result |
|---|---|
| Opus, no provider options | 200 |
| Opus, phrase list only | 200 |
| Old nested clean-style options, Ogg label or MP3 | 400 |
| Old nesting plus explicit enhanced-mode model/enabled | 400 |
| Opus, phrase list and `azure.modelOptions.transcribeStyle` | 200, 602 ms |

The style field belongs at `provider.options.azure.modelOptions.transcribeStyle`, alongside
`phraseList`, rather than beneath `enhancedMode`. The corrected production engine then
passed four live requests, retaining clean style and keyword hints. Tests now pin this
qualified shape. There is no speculative retry workaround or removal of vocabulary.

## Measurements and limits

Machine: Windows, NVIDIA RTX 3060 Ti, installed CUDA 13 runtime, Large v3 Turbo.
Fixtures: synthetic English WAVs from the previous September 16 qualification, 15.8 and
32.9 seconds. They include technical terms, numeric corrections, prohibitions and a final
sentence. No microphone recordings or history entries were replayed.

All observations, including slower results and failed probes, are retained in
[the result data](transcription-latency-results-2026-09-22.json).

| Comparison | Baseline | Changed path | What it supports |
|---|---:|---:|---|
| CUDA attention, 15.8 s fixture, median of 3 each | 562 ms | 425 ms | 24% lower decode time on this fixture |
| CUDA attention, 32.9 s fixture, median of 3 each | 1,252 ms | 1,095 ms | 13% lower decode time on this fixture |
| Final local engine, 3 rounds, stop-to-result | 354 / 605 / 1,641 ms | 0.96 / 1.05 / 4.49 ms | Completed exact-match work can remove the post-stop decode |
| Final MAI engine, 2 rounds, stop-to-result | 1,360 / 587 ms | 208 / 852 ms | Work overlaps the pause, but provider variation remains |

The early-path comparisons deliberately append approximately **900 ms of quiet before
the simulated stop**, on top of the trim's retained tail margin. They do not represent
someone stopping immediately after the final word. Starting work earlier saves time
that would otherwise follow the stop; it does not erase the speaking-to-hotkey interval.
Both paths receive identical final trimmed audio, and each successful case used one
recognition call. Transcripts matched within every comparison. CUDA cancellation followed
by a fresh full decode also succeeded with the complete expected transcript.

An earlier local run before enabling Flash Attention had an early-path outlier of
2,228 ms, slower than its paired ordinary pass. This shared workstation and small sample
cannot establish a reliable latency distribution. Confidence in a general speedup is
**moderate**, not a promise of a fixed percentage or instant delivery. Continuous speech,
short pauses, a changed final word, background noise and provider load can prevent or
reduce the gain. A stale early call consumes work and may still be billed.

The CUDA comparisons warmed both configurations, but ran off before on. Two English
fixtures do not establish recognition parity for every accent, language, model or GPU.
The isolated Flash Attention measurements omit the user's vocabulary, equally on both
sides. The production-cloud comparisons send the same five fixed terms on both sides.

## Verification

- Release build and final full xUnit suite: **387 tests passed**, including Settings
  save/apply and off-screen rendering. `Talkty.Tests/bin/ui-evidence/pause-transcription-settings.png`
  was inspected with no clipping. The six pre-existing test-source
  warnings are five nullable dereferences and one blocking-task analyzer warning.
- Headless WPF flow tests exercise late microphone buffers, one final paste, no early
  clipboard writes, settings persistence, cancellation and command-preview stop latency.
- Cloud HTTP tests verify early requests never retry or use the backup, and shutdown does
  not block on an in-flight pass. Recovery and prompt fidelity tests remain in the full suite.
- Real CUDA decode, exact-result reuse, cancellation recovery and four successful corrected
  MAI engine requests were exercised by the committed harness.
- Paid verification stayed within the announced $0.01 ceiling. Seven successful MAI
  requests represent 112 billed seconds at the current $0.10/hour rate, about $0.00312.
  Three diagnostic responses explicitly report $0.001333 total; the four engine responses
  do not expose cost through `TranscriptionResult`, so their cost is calculated from audio
  duration/rate. Five failed 400 controls did not return usage, so their billing is unknown;
  even counting all 12 attempts at 16 seconds is about $0.00534. No further paid calls
  are needed for this change.

## Reproduce

Build `tools/Talkty.LatencyCheck/Talkty.LatencyCheck.csproj` in Release. To check CUDA,
provide the existing CUDA pack in the harness output layout just as for the application.
No global install is required. Use a mono 16 kHz PCM16 synthetic WAV:

```powershell
Talkty.LatencyCheck.exe --wav short.wav --rounds 3 --check-cancel --output local.json
Talkty.LatencyCheck.exe --wav long.wav --flash-comparison --rounds 3 --output attention.json
Talkty.LatencyCheck.exe --wav short.wav --profile CloudMaiTranscribe2 --rounds 2 --run-paid --output cloud.json
```

Cloud commands require `--run-paid` and a fixture no longer than 40 seconds. They use the
saved encrypted OpenRouter key in memory and never print it. For the request-shape
regression, `--cloud-contract-probe --variants legacy-options,current-options` sends two
bounded controls; it also requires the cloud profile and `--run-paid`. The harness does
not open windows, record from the microphone or inject desktop input.

## Delivery

This is a source change for `main`; commit/CI evidence is recorded at handoff.
No public release, installer execution or replacement of the running Talkty
installation is part of this verification. The currently running app retains its old code
until an update is installed.

Later on September 22, Marko requested installation. These changes are now installed
in the local 1.4.0 package, with payload/data hashes and CUDA startup verified. See
[the 1.4.0 delivery record](RELEASE-1.4.0.md). The public release remains a draft.

## Primary references

- [Whisper.net 1.9 factory options](https://github.com/sandrohanea/whisper.net/blob/1.9.0/Whisper.net/WhisperFactoryOptions.cs), also verified against the installed DLL and NuGet XML.
- [Microsoft MAI parameters](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/mai-transcribe): model options, clean style and phrase-list placement, inspected September 22.
- [OpenRouter speech-to-text contract](https://openrouter.ai/docs/guides/overview/multimodal/stt): whole-audio JSON requests and Azure provider-option passthrough, inspected September 22.
- [MAI pricing](https://openrouter.ai/microsoft/mai-transcribe-2): $0.10/hour, inspected September 22.
