# Talkty 1.3.4

Release authorized by Marko on 16 September 2026 after the local performance build and real-use verification.

## Release notes

MAI-Transcribe 2 now uploads less audio data and starts preparing the connection earlier.

- Native Opus compression made requests 56–57% smaller on two test recordings, with identical transcripts. Smaller uploads help reduce sensitivity to a slow uplink; this is not a claim that transcription is 56–57% faster.
- Connection and encoder warm-up run concurrently and start when the cloud model becomes ready.
- Your vocabulary hints and clean transcription style are preserved. Other cloud models keep MP3, and MP3/WAV fallback remains available.
- Detailed timings make remaining cloud delays easier to diagnose.
- Upgrading keeps settings, history, downloaded models and the existing optional CUDA pack.

Network and provider response times can still vary. The local build's three real dictations reached paste in 1.25, 0.62 and 0.57 seconds; that small sample does not establish long-term consistency.

## Verification scope

The implementation and native codec were verified before release preparation: 124 tests passed, two synthetic fixtures retained all words across formats, and the published-layout engine transcribed a 15.8-second fixture in 844 ms. Three real post-install dictations used Opus with 50 vocabulary hints and succeeded without retries. No recognition behavior changed during version/release preparation.

Full measurements, limits and test costs: [latency investigation](CLOUD-LATENCY-2026-09-16.md).

## Delivery evidence

- Published 16 September 2026: [Talkty 1.3.4](https://github.com/v2matosevic/Talkty/releases/tag/v1.3.4), marked Latest. Tag target/source build: `9c0cad8642d9b2e242d4524abc16d786af0a5c9b`; includes implementation `5772301`.
- Source CI [35096896973](https://github.com/v2matosevic/Talkty/actions/runs/35096896973) passed on the exact release source. Local restore/build/test also passed: 124 tests, zero build warnings/errors.
- Clean self-contained publish: `installer/output/release-1.3.4`; assembly reports `1.3.4+9c0cad8642d9b2e242d4524abc16d786af0a5c9b`. Installer payload: 492 files, 231.43 MiB, including native `opus.dll` and third-party notices; no CUDA or unsupported platform runtimes.
- Inno compilation succeeded. Installer: `TalktySetup-1.3.4.exe`, **61,372,937 bytes**. SHA-256: `aa9a88d6434438174121a5a6f6e577a74b875ff3f6595f5684715642cf7169b7`. GitHub's asset digest and a fresh downloaded copy both matched before publication.
- Local evidence: `installer/output/build-1.3.4.log`, `installer/output/payload-1.3.4.csv`, `installer/output/download-1.3.4/TalktySetup-1.3.4.exe`. Inno reports only the existing unused `DataDirPage` hint.
- Public `version.json` is advanced after release publication, with update text describing smaller requests and remaining provider variability. Existing installations receive it through their normal update check. The optional CUDA pack release is unchanged.
- No further paid transcriptions were needed for release preparation. The release changes only the version/packaging/documentation relative to the verified local implementation; its live evidence is reused with that scope.
- Marko's local app remains the verified `1.3.3-latency.20260916` build installed earlier; this publication did not run another installer or claim a new local installation.
