# Talkty 1.5.0

Stable Windows installation and open-source release update requested by Marko on
3 October 2026. Release preparation is in progress. Talkty 1.4.0 remains installed
and public until the delivery evidence below records successful replacement.

## Release notes

- The main window shows the microphone action, recording state and selected
  local/cloud model more clearly. Output is a full clickable destination card,
  with accurate cursor, clipboard, history-only or selected ADE draft feedback.
  The audio meter appears during recording.
- Search history by final text or the original spoken transcription, without
  shortening or rewriting stored entries. Empty history and no matching entries
  have separate messages. Small windows retain scrolling and virtualized history.
- Pause checks examine the existing audio buffer without allocating a copied
  tail. A focused 1,000-check benchmark eliminated 51,224,000 temporary bytes.
  Obsolete early recognition cancels when speech resumes. The selected model,
  complete take and exact-audio result reuse are preserved. This is not evidence
  of a faster overall transcription distribution.
- Optional direct dictation to a named Hephaestus ADE agent draft. The entire
  instruction is staged for review without replacing the clipboard or sending a
  turn. Recording freezes its destination. Unverified handoffs remain encrypted
  locally and retry with the same destination and operation ID.
- Command delivery uses stable operation IDs and receipt reconciliation instead
  of blindly repeating a possibly completed action after a lost response.
- Corrected the public privacy document and installer disclosure to describe
  optional cloud processing, text in logs, encrypted recovery and ADE draft delivery.

## Ecosystem requirements and privacy

Direct draft delivery requires the matching ADE protocol-v1 capture receiver
(implemented in ADE source `3f029e89`). The public ADE 0.0.108 and this PC's older
installed ADE do not include it. This Talkty release does not install ADE or Athena
Desktop and does not claim an installed hub connection. WinSnipper 0.9.0 already
contains its optional screenshot companion; screenshots and dictation meet in the
same selected unsent draft once a compatible receiver is installed.

Durable command receipts require the matching Hermes operation-receipt update
(`dc02d887b` in Athena source). Older services retain conservative uncertainty
handling rather than unsafe automatic replay. Ordinary dictation needs neither
integration. No generic terminal or Athena Desktop conversation destination is
included in this release.

Local recognition remains the default. Cloud transcription, Prompting and its
optional checks use configured external providers and can cost per request.
Cancelled early cloud recognition may already have been accepted and billed.
Direct ADE delivery shares the completed text with the explicitly selected local
agent draft; Talkty never automatically submits it. Saved uncertain capture
handoffs use Windows current-user encryption.

## Verification

Implementation source: `2e8ffe1`, source handoff `a3cf517`, performance/interface
source `0e32998`. The last complete implementation suite passed 405 tests on
3 October. Release-source restore, build, full tests and GitHub CI will be recorded
below. Six existing test-source warnings are known; no application compilation
warnings were introduced by these changes.

Production WPF views were rendered and inspected at 380 x 420, 420 x 520 and
720 x 640, including ready, recording, transcribing, loading and no-match states.
Tests cover full-text search, keyboard actions, virtualization, exact-result reuse,
early cancellation, frozen capture destinations and encrypted delivery recovery.
No private audio was replayed and no paid model request was used for release
preparation. [Performance and appearance evidence](PERFORMANCE-APPEARANCE-2026-10-03.md).

![Main window](evidence/performance-ui-20261003/main.png)

Capture boundary and native/browser evidence:
[first capture implementation](CAPTURE-IMPLEMENTATION-2026-10-03.md).

## Package and publication evidence

Pending: exact compiled commit, Release/CI results, fresh self-contained payload,
native Opus/Vulkan validation, installer size/hash, downloaded asset hash and public
Latest verification. The optional CUDA pack is version-independent and will not
be replaced. Public `version.json` remains at 1.4.0 until the installer is public.
The standard installer remains unsigned, as documented in the installation guide.

## Windows installation

Pending: preserve the current installation and settings/history/recovery/model/
CUDA hashes; receive explicit permission to close the running Talkty instance;
upgrade through the official Inno installer; verify exit, registry and all payload
hashes; relaunch normally and check startup version, hotkeys and model/backend.
This PC's existing all-users installation requires Windows administrator approval.

Installed microphone/desktop-input behavior and a complete clean-machine install/
uninstall are separate checks and are not established by headless tests.
