# Recording and output reliability

Local improvement pass, started 4 September 2026 and completed 5 September 2026.

Public release update, 5 September 2026: all completed work is now pushed and
[1.3.2 is published](https://github.com/v2matosevic/Talkty/releases/tag/v1.3.2).
The update notice now advertises 1.3.2. [Release verification](RELEASE-1.3.2.md)
supersedes the historical pending-approval and 1.3.0 public-status notes below.

Current handoff: version 1.3.2 supersedes this historical 1.3.1 installation.
Read [VOCABULARY-ACCURACY.md](VOCABULARY-ACCURACY.md) for the accuracy fix and final
delivery evidence, and [UX-PERFORMANCE-2026-09.md](UX-PERFORMANCE-2026-09.md) for the
desktop changes. The combined app is installed, with 99 tests passed. Public release
metadata remains at 1.3.0.

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

No GitHub release has been published. `version.json` remains at the currently published 1.3.0 and must be updated when the public release is made.

## Resume next session

- Collect Marko's live dictation/cancellation feedback before changing microphone processing or decoding settings.
- Use representative audio and expected transcripts to assess recognition accuracy and latency. Existing code and headless tests do not establish those measurements.
- Keep the installed 1.3.2 build and personal vocabulary corrections. Collect repeatable mishearings before switching away from Turbo.
- Public release remains separate: when authorized, publish the verified 1.3.2 installer, then update `version.json` with matching release notes. Do not announce an update before its installer is available.
- The 1.3.0 installer remains in `installer/output` as a rollback artifact. Installation logs and fingerprints contain no settings values or audio.
