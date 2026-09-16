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

Release build, installer, CI and publication evidence will be recorded here as each check completes. The public update manifest is advanced only after the installer is available.
