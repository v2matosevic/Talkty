# Recording and output reliability

Local improvement pass, started 4 September 2026 and completed 5 September 2026.

**Current handoff, 16 September 2026: [1.3.3 is installed and published](RELEASE-1.3.3.md).**
Everything below this section is a historical record of the 1.3.1/1.3.2 work; where it
says a release is pending or `version.json` sits at 1.3.0, read the current status
section instead. Companion records: [VOCABULARY-ACCURACY.md](VOCABULARY-ACCURACY.md)
(accuracy fix), [UX-PERFORMANCE-2026-09.md](UX-PERFORMANCE-2026-09.md) (desktop changes)
and [RELEASE-1.3.2.md](RELEASE-1.3.2.md) (the previous release).

## End-of-day status, 16 September 2026

- Installed and public: **1.3.3**. `B:/Talkty` is registered as Talkty 1.3.3, the build is
  `1dc462d`, the release tag sits at `c60680f`, and `version.json` advertises 1.3.3, so
  existing installs are offered the update. Nothing is pending.
- What shipped: MAI-Transcribe 2 is the recommended cloud model; cloud audio uploads as
  48 kbps MP3 instead of WAV; the encoder and HTTPS connection warm up at recording start;
  the saved vocabulary reaches MAI as its phrase list (first 50 terms, additions first).
- Verification: 120 tests pass in Release, 488/488 installed payload hashes match, CI passed
  on both pushed commits, and GitHub's asset digest plus a fresh download match the local
  installer SHA-256 `6cb7b290…`. Two real dictations on the final build returned in 1.63 s
  and 0.66 s. Full evidence and limits: [RELEASE-1.3.3.md](RELEASE-1.3.3.md).
- Invariants worth keeping: do not revert cloud uploads to WAV (upload size, not inference,
  dominated cloud latency), and never send more than 50 phrases to MAI (the 51st returns a
  provider 400). `ModelProfile` is still persisted by number; MAI is enum 16.
- Tooling: `tools/cloud-engine-check.ps1` drives the real cloud engine against a WAV file
  with no UI or microphone. It spends real money on the saved OpenRouter key, so keep test
  clips short; a day of format benchmarking cost about $0.09.
- Open items: long real dictations on 1.3.3 are unmeasured; MAI does not support Croatian or
  Serbian; the WAV fallback for Windows without Media Foundation has not run on such a
  machine; CI reports a Node 20 deprecation notice for `actions/checkout@v4` and
  `actions/setup-dotnet@v4`; a `.gitignore` change adding `.clipboard-images/` is
  uncommitted and was not made by this session.
- Next session: time one long real dictation before considering parallel chunked uploads,
  and consider adding "OpenRouter" and "version2.hr" to the vocabulary (Marko's call; both
  still mis-transcribe because they are not in his list).

## End-of-day status, 5 September 2026

- Installed and ready: `B:/Talkty/Talkty.App.exe`, version 1.3.2. No installation step remains.
- Local installer: `installer/output/TalktySetup-1.3.2.exe`. Verified publish: `Talkty.App/bin/Release/verified-1.3.2-20260905/`.
- Completed verification: 99 tests pass in Debug and Release; three off-screen desktop sizes inspected; 488 installed payload hashes match. Confidence is high for these checks. Recognition accuracy and real microphone feel remain unmeasured.
- Personal settings include the phrase-specific Claude Code correction. Keep the current Turbo model and those settings; the correction does not replace standalone “cloud”.
- All implementation and delivery work is committed locally. GitHub sync awaits the explicit push approval already requested; the end-of-day documentation request does not authorize that separately blocked action.
- Public GitHub release was checked directly: v1.3.0. Keep `version.json` at that version until a newer installer is publicly available. Publishing 1.3.2 is a separate decision, not a requirement for using the installed app.
- Next session starts with the two-minute native recording/cancellation/history check in [UX-PERFORMANCE-2026-09.md](UX-PERFORMANCE-2026-09.md), followed by any repeatable mishearing examples. Do not repeat installation or rebuild merely to resume work.

Implementation/delivery commits: `46222ca` (recording reliability), `32f922c` (vocabulary), `a39c391` (1.3.2 delivery), `b18a390` (desktop UX/performance), `e0b49aa` (installed UI evidence), `0b2a27a` (personal correction and accuracy handoff). Detailed test limits and history-preservation evidence remain in the linked documents.

## Changes

- Each new recording releases the previous microphone device. Failed starts also release it. Late callbacks from a retired device cannot add samples to, or complete, a new recording.
- Stopping still accepts the final audio buffers. Flush waits also work after an earlier stop. Driver exceptions and flush timeouts return failure instead of being treated as success; usable partial recordings remain available with a warning.
- The initial float buffer reserves 960,000 bytes instead of 7,680,000 bytes, a reduction of 6,720,000 bytes per recording buffer. Longer recordings grow as needed. This is an allocation calculation, not a measured reduction in total process memory.
- Digital silence skips transcription. Quiet nonzero audio is preserved. This is deliberately not a voice-activity detector or a new silence threshold.
- Cancelling during audio flush, transcription, or prompt refinement discards pending output. Auto-paste and Prompting no longer overwrite the clipboard with an unfinished first segment. Clipboard-only dictation retains early segment copying.
- Empty text after hallucination removal cannot clear the clipboard, produce an empty history entry, or invoke prompt refinement.
- Clipboard copy failures and history-only mode no longer report a successful copy. Failed clipboard preparation after a focus switch aborts auto-paste. Partial or failed Windows input delivery returns failure.
- Auto-paste receives the recording's cancellation token and checks it during modifier-key waits and immediately before sending paste input.

## Evidence and verification

The original suite passed 43 tests. The expanded suite passes 74: 11 recorder lifecycle tests, 15 transcription-flow cases, and 5 paste-commit tests were added.

The flow tests run the real MainViewModel on a WPF dispatcher with simulated devices, engines, clipboard, paste destination, and persistence. They do not open windows, inject keyboard input, record the microphone, call cloud services, or modify the user's history/settings.

Commands:

```powershell
dotnet test Talkty.Tests/Talkty.Tests.csproj --no-restore --verbosity minimal
dotnet publish Talkty.App/Talkty.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o Talkty.App/bin/Release/verified-1.3.1-20260905
& 'C:/Program Files (x86)/Inno Setup 6/ISCC.exe' '/DMyAppSourcePath=B:/Coding/Talkty/Talkty.App/bin/Release/verified-1.3.1-20260905' 'installer/TalktySetup.iss'
git diff --check
```

The late-August log samples examined showed warmup warnings and occasional slow inference. The defects above were confirmed by code review and regression tests, not attributed to an unverified current user complaint.

Confidence is high for the deterministic fixes covered by these tests. Live microphone behavior and target-application paste behavior still need a manual check. No speech-recognition accuracy score or real-world speed improvement is claimed; models, languages, and cloud providers were not changed.

Cancellation prevents pending work. It cannot undo paste input already sent, or a first segment already copied in clipboard-only mode. Recognition-quality work needs representative recordings and expected transcripts before changing decoding or microphone processing.

## Two-minute manual check

1. Use the updated installation at `B:/Talkty/Talkty.App.exe`.
2. Record two short sentences consecutively, stopping immediately after the last word. Confirm both endings arrive and the second recording contains no words from the first.
3. Enable Prompting for a recording, stop, then press Escape during refinement. Confirm no text is pasted.
4. Record once with the microphone muted. Confirm the no-audio notice when the device supplies digital silence.

## Installation on this PC

Version 1.3.1 was installed at `B:/Talkty` on 5 September 2026 at the user's request. The Inno Setup upgrade exited successfully without requiring a Windows restart, and Windows registered version 1.3.1 at that point. The later 1.3.2 upgrade is recorded in the current handoff above.

All 488 installed payload files match the published build by SHA-256. Settings (excluding launch counters) and history match their pre-install fingerprints. The existing CUDA pack remains present. The installer is `installer/output/TalktySetup-1.3.1.exe`; installation and verification logs are alongside it.

The installed app was opened successfully. Startup logs confirm Alt+Q registered, the selected model loaded on CUDA, and processor warmup completed. The window was subsequently closed to the tray, where Talkty remains running.

The installer accepts an optional `MyAppSourcePath` compiler definition so it can package the freshly verified output directly. History card success notifications now come from the successful clipboard operation, preventing the UI from displaying success after a failed copy.

(Historical, 5 September 2026: no release had been published at that point and `version.json` still pointed at 1.3.0. Both 1.3.2 and 1.3.3 have since been released, and `version.json` advertises 1.3.3.)

## Resume next session

- Collect Marko's live dictation/cancellation feedback before changing microphone processing or decoding settings.
- Use representative audio and expected transcripts to assess recognition accuracy and latency. Existing code and headless tests do not establish those measurements.
- Keep the installed 1.3.2 build and personal vocabulary corrections. Collect repeatable mishearings before switching away from Turbo.
- (Done since: 1.3.2 was published on 5 September and 1.3.3 on 16 September, each time by publishing the verified installer first and updating `version.json` only afterwards. Keep that order for the next release.)
- The 1.3.0 installer remains in `installer/output` as a rollback artifact. Installation logs and fingerprints contain no settings values or audio.
