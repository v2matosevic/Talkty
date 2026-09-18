# Talkty 1.3.5

Stable open-source release and installation authorized by Marko on 18 September 2026.

## Release notes

Cloud transcription failures no longer make your recording disappear.

- Failed cloud recordings stay encrypted on your PC, survive app restarts, and appear in Talkty with Retry and Discard controls. A new recording never replaces an older failed take.
- Choose an automatic backup model in Settings > Cloud & Prompting. Qwen3 ASR Flash is the default backup; Off and other cloud models are available. Your primary model stays unchanged.
- Temporary provider errors switch directly to a compatible backup instead of waiting and sending another request to the same busy provider. Authentication, insufficient balance, cancellation and no-speech results do not trigger a backup. Backup requests cost per use.
- Added Qwen3 ASR 1.7B as a separate cloud option. Qwen Flash keeps MP3 uploads for accuracy; MAI keeps native Opus, vocabulary hints and clean transcription.
- Retrying from Talkty copies the recovered transcript when clipboard output is enabled, without auto-pasting into an old window. The recording stays recoverable if saving history fails.
- Upgrading preserves settings, transcription history, downloaded models, recovery recordings and the optional CUDA pack.

The default Qwen Flash backup supports the catalog's 11 languages, including English, but not Croatian or Serbian. Unsupported explicit languages and identical primary/backup selections are skipped. Previously overwritten recordings cannot be recovered retroactively. No new speed or accuracy guarantee is claimed.

## Verification

Implementation commit: `b1ed66f`. Prior local build: `1.3.4-recovery.20260918`.

The stable source passed restore, Release build (zero warnings/errors), and all 143 tests locally and on GitHub CI. Tests include simulated provider failures, both MAI/Qwen fallback directions, cancellation, encrypted recovery across store reloads, disk/history-save failures, independent failed takes, retry without auto-paste, and settings persistence. WPF recovery and Settings layouts were rendered and inspected. Recognition behavior is unchanged from the verified local patch; its bounded live evidence is reused.

Live checks on the same synthetic 15.8-second recording: MAI with vocabulary hints returned the complete expected text in 1.233 seconds; Qwen Flash MP3 returned in 3.473 seconds with one proper-name error. Qwen 1.7B timed out, and a smaller Qwen Opus upload lost C++, so neither experiment was selected for the default path. These are individual observations, not a performance benchmark. No new paid tests are needed for version-only packaging.

Detailed behavior, model sources, costs and limitations: [cloud recovery investigation](CLOUD-RECOVERY-2026-09-18.md).

## Delivery evidence

- Published stable and marked Latest on 18 September 2026: [Talkty 1.3.5](https://github.com/v2matosevic/Talkty/releases/tag/v1.3.5). Release tag and compiled source: `20de6b1530debde3617b0e961e5615fc998a5eb1`, including implementation `b1ed66f`.
- Exact-source [GitHub CI run 35372550289](https://github.com/v2matosevic/Talkty/actions/runs/35372550289) passed restore, build and tests. Local gates also passed: 143 tests, zero warnings/errors.
- Fresh self-contained publish: `installer/output/release-1.3.5`; product version `1.3.5+20de6b1530debde3617b0e961e5615fc998a5eb1`. Installer payload contains 492 files, native Opus and third-party notices, with no CUDA or unsupported platform runtimes. Manifest: `installer/output/payload-1.3.5.csv`.
- Inno Setup compiled successfully with only the existing unused `DataDirPage` hint. Installer: `TalktySetup-1.3.5.exe`, **61,384,462 bytes**, SHA-256 **`0cdf807439068effdba8ed1155eee0e87d894707b9d7c2f64c69ee62cc1bbaed`**. GitHub asset digest and a freshly downloaded copy both matched before publication. Build log: `installer/output/build-1.3.5.log`.
- Installed the downloaded release artifact on this PC using the elevated Inno installer; exit **0**. HKLM registration now reports **1.3.5**, installed at `B:/Talkty`. All **492/492** installed payload files match the manifest. Settings, history, existing recovery files and the four required optional CUDA files were hash-verified unchanged. Evidence: `installer/output/install-1.3.5.log`, `install-verification-1.3.5.json`, `data-before-1.3.5.json`, `cuda-before-1.3.5.json`.
- Relaunched as PID 35572. Startup log `talkty_2026-09-18_19-12-04.log` confirms version 1.3.5, Alt+Q, CUDA LargeTurbo loaded, and warm-up completed. The current primary model was preserved. No live microphone or cloud request was needed for packaging validation.
- Public `version.json` advances to 1.3.5 only after publication, so existing installations can discover the stable release. The optional CUDA-pack release is unchanged.
