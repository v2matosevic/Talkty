# Reddit

Reddit rewards honesty and hates ads. Post as the maker, lead with "it's free and
open source," answer questions in the comments, and read each subreddit's
self-promotion rules first (some require a flair or a ratio of non-promo activity).
No em-dashes. Attach the horizontal video or the hero image where the sub allows it.

Links:
- Windows: https://github.com/v2matosevic/Talkty
- macOS: https://github.com/v2matosevic/talkty-mac

---

## r/LocalLLaMA

**Title:** Talkty: free, open-source local speech-to-text for Windows and macOS (Whisper, fully on-device)

**Body:**
I wanted dictation that does not send my microphone to someone's server, so I built Talkty. Whisper runs locally, the audio is discarded the moment it becomes text, and nothing leaves the machine unless you deliberately turn on a cloud feature.

- Local Whisper with GPU acceleration: CUDA and Vulkan on Windows, Metal and the Apple Neural Engine on macOS. CPU works too.
- Press a global hotkey, speak, and the text is on your clipboard (optional auto-paste at the cursor).
- Custom coding vocabulary and post-processing that strips Whisper's "thanks for watching" style hallucinations.
- Optional, off by default: cloud transcription through OpenRouter, and a Prompting mode that rewrites a rambling dictation into a structured prompt for a coding agent (Claude Code, Cursor, Codex). Your API key is encrypted on device.

Both versions are MIT licensed. Feedback and PRs welcome. Happy to answer anything about the pipeline.

---

## r/SideProject

**Title:** I built Talkty, a free local speech-to-text app for Windows and macOS. Fully open source.

**Body:**
Press a hotkey, talk, and your words land on the clipboard, ready to paste anywhere. It runs entirely on your device with Whisper. No account, no cloud, no telemetry.

It started as a tool for coding by talking to an AI agent (there is an optional mode that turns dictation into a clean prompt), but at its core it is just a fast "hold a key and talk" tool that works in any app.

MIT licensed, both platforms. I would genuinely like to hear what you think.

---

## r/opensource

**Title:** Talkty: MIT-licensed local speech-to-text for Windows and macOS, no telemetry

**Body:**
Talkty is a local-first dictation tool. Whisper runs on your device, audio is never written to disk or sent anywhere, and there is no account or telemetry. Optional cloud and AI-prompt features exist but are off by default.

Native builds for each platform (.NET and WPF on Windows, Swift and SwiftUI on macOS), both MIT. Issues and pull requests are welcome, and there is a CONTRIBUTING guide in each repo.

---

## r/macapps

**Title:** Talkty for macOS: native Apple Silicon speech-to-text, on-device, free and open source

**Body:**
A native menu-bar dictation app for Apple Silicon. Press a hotkey, speak, and the text is on your clipboard (optionally typed at the cursor). Whisper runs with Metal, and optionally the Neural Engine, so a long clip transcribes in about a tenth of a second. Nothing leaves your Mac.

Heads up: the build is self-signed, not yet notarized, so on first launch you right-click and choose Open (or clear the quarantine attribute). Notarization and a Homebrew cask are on the roadmap.

MIT licensed: https://github.com/v2matosevic/talkty-mac

---

## r/windowsapps

**Title:** Talkty: free local speech-to-text for Windows, powered by Whisper (open source)

**Body:**
Press a global hotkey, speak, and your words are on the clipboard. Everything runs locally with optional GPU acceleration (CUDA on Nvidia, Vulkan on AMD and Intel). No account, no cloud, no telemetry.

The installer is per-user and needs no admin rights. It is not code signed yet, so SmartScreen will warn once (More info, then Run anyway).

MIT licensed: https://github.com/v2matosevic/Talkty

---

## Note on r/programming and r/coding

These remove most "I built X" posts. Only post if you can frame it around something
technical and discussion-worthy (for example the local Whisper pipeline or the
prompt-rewriting design), and expect strict moderation. The dev-tool and platform
subs above are a better fit.
