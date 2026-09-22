# Talkty 1.4.0

Local Windows installation and open-source release preparation requested by Marko on
22 September 2026. The installer and GitHub draft are prepared. Windows canceled the
administrator prompt before installation, so the previous 1.3.5 app was relaunched.
Local installation still needs an approved administrator prompt. Public Latest remains
1.3.5 until publication is separately requested.

## Release notes

- Transcription can start during pauses while preserving the full recording and selected
  model. Early text is used only when the final flushed audio matches exactly; otherwise
  the normal full pass runs. Disable this under Settings > Behavior. Cloud can bill one
  extra pass, including cancelled takes. Nothing is pasted provisionally.
- CUDA uses Flash Attention. On one RTX 3060 Ti and two synthetic English fixtures,
  median decoding was 13–24% faster with identical transcripts. Paused local takes can
  finish almost immediately after Stop; cloud results varied. These are bounded findings,
  not a guaranteed speedup for every recording. [Measurements](TRANSCRIPTION-LATENCY-2026-09-22.md).
- Corrected MAI's clean-style request options, which caused provider HTTP 400 errors.
  Native Opus uploads, full context, vocabulary hints and clean output remain enabled.
- Restored the opt-in Prompting control in Settings > Cloud & Prompting. The earlier
  pill cleanup removed its button but had not supplied a usable replacement. The
  recording now uses the saved setting. Prompting adds a paid text request and delay;
  it stays off by default, and command mode bypasses it.
- Includes optional [prompt fidelity checks](PROMPT-FIDELITY.md) and advanced
  [prompt planning](PROMPT-PLANNING.md). They only apply to Prompting. Planning defaults
  off; fidelity's default Record only mode can make a bounded text request after a
  generated prompt is delivered.
- Includes opt-in [command mode](COMMAND-MODE.md), with a separate shortcut, provisional
  local recognition, service discovery, captured target window, progress and result on
  the pill. It requires an external local service and does not paste commands into apps.
- Removed the blocking command-preview shutdown wait. Native decoder work is serialized,
  cancellation unwinds before reuse, and shutdown does not block the dispatcher.
- Updated installer privacy text and source-build instructions. The checked installer
  script now uses a fresh non-single-file publish, verifies native payloads, and produces
  a file manifest and SHA-256 checksum. CUDA remains an optional separate pack.

- Fixed the uninstaller's Keep data choice: deletion now checks the actual answer.
  The old code asked but did not condition its deletion entry. Keeping data is the
  default, including when confirmation messages are suppressed. An isolated installer
  test proved both Keep and Remove against repository fixtures, with all four process
  exits 0. User data removal runs in the uninstall event, not a Setup-time Check entry.
  The existing Windows installation is upgraded in place; its uninstaller is not run.

Upgrading preserves settings, history, encrypted recovery files, downloaded models and
the optional CUDA pack. Recognition language and primary model are not changed.

## Verification and packaging

Behavioral baseline: `f6a8f53`, 387 passing tests and GitHub CI 35758274073.
The 1.4.0 Release build and **389 tests passed locally**, including saved Prompting
activation, command isolation and off-screen Settings rendering. Six pre-existing
test-source warnings remain. Exact-source CI will be recorded below. Earlier paid model
measurements are reused because this packaging/control fix does not change the engines,
their request payloads or audio processing. No further paid verification is required.

## Package and draft evidence

- Compiled source: `9a9f7b4c7ce38a6027550d863d45e2b50d02064d`.
  [Exact-source CI 35764624933](https://github.com/v2matosevic/Talkty/actions/runs/35764624933)
  passed restore, Release build and tests. The C# release suite has 389 tests; the two
  isolated uninstall cases separately verified Keep and Remove, with exit 0 throughout.
- Fresh self-contained publish: `installer/output/release-1.4.0-verified`.
  Product version `1.4.0+9a9f7b4c7ce38a6027550d863d45e2b50d02064d`.
  The payload manifest contains 492 files, including Vulkan, native Opus and third-party
  notices, with no bundled CUDA or unsupported platform payloads.
- Installer: `TalktySetup-1.4.0.exe`, **61,508,891 bytes**. SHA-256:
  `6567995affdc2a5fd4978525364bdd437cf72c89f7deff7d47b03a2acaccc643`.
  The final Inno compile completed successfully without warnings. Build log:
  `installer/output/build-1.4.0-verified.log`; manifest: `payload-1.4.0.csv`.
- [GitHub draft](https://github.com/v2matosevic/Talkty/releases/tag/untagged-4b3a40947aa2ce616063)
  targets that source commit and includes the installer and `.sha256` file. The GitHub
  asset digest and a freshly downloaded installer both match the local installer hash.
- Uninstall fixture evidence: `installer/output/uninstall-check-e3b5717c84244a32982798269fe9a5e1/results.json`.
  These fixtures register no application and never access the installed Talkty or real
  user data. They are not a clean-machine test of the complete application's uninstall.
- The restored Prompting control was rendered and inspected off-screen at 680 x 560:
  `installer/output/settings-1.4.0/settings-prompt-check.png`. No desktop input was injected.

## Windows installation attempt

- The previous app was confirmed idle and backed up at
  `installer/output/pre-1.4.0-app` before the attempted upgrade.
- Preservation snapshots cover 53 settings/history/recovery/model files and 13 CUDA
  files. Local hash manifests: `data-before-1.4.0.json` and `cuda-before-1.4.0.json` in
  `installer/output`. They contain hashes and paths, not copied credentials.
- Windows canceled the UAC request before the elevated installer started. No successful
  installer exit, post-install payload verification or 1.4.0 installation is claimed.
  There is no completed installer log for this attempt.
- The previous `B:/Talkty/Talkty.App.exe` was relaunched as PID 84928. Windows registration
  remains 1.3.5. Startup log `talkty_2026-09-22_20-17-41.log` confirms the hotkey, CUDA GPU
  model load and completed warm-up. Its product version is
  `1.3.5+c1ad900c878e5ae61212636d58e379ae8f821f33`.
- The owner was asked whether to display the administrator prompt again. No further
  installation attempt is made without that response. No live microphone test or public
  application release is implied.
