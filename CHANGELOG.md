# Changelog

User-facing changes, newest first. Dates are when the work landed.

## [1.1.5] - 2026-06-20

- History now keeps both halves of a Prompting entry: what you said and the prompt
  it generated, shown together with a PROMPT badge. Click the entry to copy the
  prompt, or click the "You said" line to copy just your original words. Plain
  transcriptions look the same as before.

## [1.1.4] - 2026-06-20

- The default Prompting model is now Gemini 3.1 Flash Lite, chosen for speed. If a
  fast model ever drops detail, the completeness guard escalates to a stronger one
  automatically.

## [1.1.3] - 2026-06-20

- Fixed: the Prompting model you pick now persists across restarts.
- Added a completeness guard that re-runs on a stronger model when a result looks
  summarized rather than expanded.

## [1.1.2] - 2026-06-20

- The Prompting model is now selectable in Settings.
- Settings reorganized into clear Local and Cloud groups, with the OpenRouter API
  key in its own section.

## [1.1.1] - 2026-06-19

- Prompting no longer summarizes your dictation. It keeps every detail and only
  reformats, dropping filler and resolving self-corrections.

## [1.1.0] - 2026-06-19

- Cloud transcription via OpenRouter (opt-in): GPT-4o Transcribe, Whisper Large V3,
  Qwen3 ASR, and more for higher accuracy when you want it. Local Whisper stays the
  private, offline default.
- Prompting mode (opt-in): turn a dictation into a structured prompt for a coding
  AI agent, with a fast model and automatic fallback.
- The OpenRouter API key is stored encrypted on device (Windows DPAPI).
- Fixed a clipboard stall that delayed pasting on the cloud path.

## [1.0.11] - 2026-04-20

- Fixed the loss of the last fraction of a second of speech, the most common cause
  of "it cut off the end of what I said".
- More accurate hallucination stripping (no longer deletes a real "thank you" or
  "bye"), faster transcription, and many small fixes. Added a test suite.

## [1.0.10] - 2026-03-27

- Rewrote auto-paste to be non-invasive. No more activating menus or search bars in
  other apps, and paste now completes in a few milliseconds.

## [1.0.9] - 2026-03-24

- Custom coding vocabulary in two layers: a Whisper prompt plus deterministic text
  replacements (for example "cube cuddle" becomes kubectl).
- Reliable auto-paste, smart segment joining, hallucination stripping, and
  punctuation cleanup.

## [1.0.8] - 2026-03-12

- Faster transcription: silence trimming, streaming the first segment, model warmup,
  and greedy decoding.
- Added quantized "Lite" models for CPU-only machines.
- Atomic, crash-safe writes for settings and history.

## [1.0.7] - 2026-02-27

- Added a Vulkan GPU backend for AMD and Intel iGPUs. Talkty now auto-detects CUDA,
  then Vulkan, then falls back to CPU.

## [1.0.5] - 2026-01-05

- ESC cancels an active recording.
- Configurable volume ducking level.

## [1.0.4] - 2026-01-05

- Volume ducking: lower system volume while recording, with smooth fades.

## [1.0.3] - 2025-12-18

- Model loading indicator, full language names in the dropdown, and better GPU
  detection.

## [1.0.2] - 2025-12-18

- Explicit language selection and fixes for non-English transcription.

## [1.0.1] - 2025-12-07

- Crash logging and an update notification system.

## [1.0.0] - 2025-12-07

- Initial release. Local Whisper transcription, a customizable global hotkey,
  multi-monitor overlay, auto-paste, in-app model downloads, and an installer.
