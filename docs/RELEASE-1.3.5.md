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

The implementation passed 143 Release tests, including simulated provider failures, both MAI/Qwen fallback directions, cancellation, encrypted recovery across store reloads, disk/history-save failures, independent failed takes, retry without auto-paste, and settings persistence. WPF recovery and Settings layouts were rendered and inspected. The same source will be rebuilt and tested for the stable version.

Live checks on the same synthetic 15.8-second recording: MAI with vocabulary hints returned the complete expected text in 1.233 seconds; Qwen Flash MP3 returned in 3.473 seconds with one proper-name error. Qwen 1.7B timed out, and a smaller Qwen Opus upload lost C++, so neither experiment was selected for the default path. These are individual observations, not a performance benchmark. No new paid tests are needed for version-only packaging.

Detailed behavior, model sources, costs and limitations: [cloud recovery investigation](CLOUD-RECOVERY-2026-09-18.md).

## Delivery evidence

Release source CI, installer hash, public download verification and local installer verification will be recorded here after those steps complete.
