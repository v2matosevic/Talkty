# Talkty 1.4.0

Local Windows installation and open-source release preparation requested by Marko on
22 September 2026. **1.4.0 is installed and running locally and is the public Latest
release.** Marko explicitly requested publication after the local installation was verified.

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
test-source warnings remain. Exact-source CI is recorded below. Earlier paid model
measurements are reused because this packaging/control fix does not change the engines,
their request payloads or audio processing. No further paid verification is required.

## Package and publication evidence

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
- [Public GitHub release](https://github.com/v2matosevic/Talkty/releases/tag/v1.4.0)
  was published on September 22 at 19:32:55 UTC and marked Latest (not a prerelease).
  The public tag points to the compiled source above. It includes the installer and
  `.sha256` file; the asset digest matches the locally installed and previously downloaded
  package. The unauthenticated Latest API and public release page both resolve to 1.4.0.
- Root `version.json` advances to 1.4.0 after publication, keeping its download link on
  GitHub's Latest release. Existing installations can discover it through their update check.
- Uninstall fixture evidence: `installer/output/uninstall-check-e3b5717c84244a32982798269fe9a5e1/results.json`.
  These fixtures register no application and never access the installed Talkty or real
  user data. They are not a clean-machine test of the complete application's uninstall.
- The restored Prompting control was rendered and inspected off-screen at 680 x 560:
  `installer/output/settings-1.4.0/settings-prompt-check.png`. No desktop input was injected.

## Windows installation

- The previous app was confirmed idle and backed up at
  `installer/output/pre-1.4.0-app` before the attempted upgrade.
- Preservation snapshots cover 53 settings/history/recovery/model files and 13 CUDA
  files. Local hash manifests: `data-before-1.4.0.json` and `cuda-before-1.4.0.json` in
  `installer/output`. They contain hashes and paths, not copied credentials.
- Windows canceled the first UAC request before setup started, and the previous app
  was reopened. Marko explicitly requested another prompt; the retry was approved.
- The official elevated installer completed with **exit 0** at 21:18 on September 22.
  Windows registration now reports **1.4.0**, installed at `B:/Talkty`; no Windows reboot
  was required. Installer log: `installer/output/install-1.4.0.log`.
- **492/492 installed payload hashes match** the verified package. All **53 data/model
  files and 13 CUDA files** were hashed again after installation and match their
  preservation snapshots, before the app was relaunched. The unchanged model/runtime
  pre-update hashes were reused on the retry when their file timestamps were unchanged;
  the post-install checks reread every file, approximately 6 GB in total.
- Relaunched normally as PID **2824**. Startup log `talkty_2026-09-22_21-23-59.log`
  confirms version 1.4.0, registered hotkey, CUDA model loaded, **GPU flash attention
  enabled**, and warm-up completed. The primary model remains Large v3 Turbo, language
  English, GPU and auto-paste enabled. The new pause-recognition setting defaults on;
  Prompting remains off. No user setting was changed to force those defaults.
- Verification record: `installer/output/install-verification-1.4.0.json`. The app's
  product version matches the compiled source listed above. No live microphone or
  desktop-input test was performed for installation. Public publication was subsequently
  authorized and completed as recorded above.
