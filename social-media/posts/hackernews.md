# Hacker News (Show HN)

HN rewards substance and honesty and punishes marketing language. Keep the title
plain, post the body as the first comment, and be in the thread to answer questions.
No em-dashes.

---

## Title

Show HN: Talkty – local, private speech-to-text for Windows and macOS (MIT)

## URL

https://github.com/v2matosevic/Talkty

## First comment

I built Talkty because every dictation tool I tried streamed my microphone to a server. That is a non-starter for client work and half-formed ideas, so I wanted the opposite: speech to text that runs entirely on the machine.

You press a global hotkey, speak, and the text lands on your clipboard (optionally typed at the cursor). Whisper runs locally. The audio is held in memory only until it becomes text, then discarded. No account, no telemetry.

Some details that might interest this crowd:

- Windows build is .NET 8 and WPF using Whisper.cpp, with runtime auto-detection of CUDA, then Vulkan (so AMD and Intel iGPUs get acceleration too), then CPU. The macOS build is a separate native Swift app using whisper.cpp with Metal and an optional Core ML encoder on the Neural Engine.
- Post-processing matters more than I expected: re-joining sentences split by a pause, and stripping Whisper's end-of-clip hallucinations ("thanks for watching", "[MUSIC]") without deleting a legitimate "thank you" in real speech.
- Two opt-in features, both off by default and behind one OpenRouter key that is encrypted on device: cloud transcription, and a Prompting mode that rewrites a rambling dictation into a structured prompt for a coding agent. The prompt builder is the part I went deepest on, since the failure mode was summarizing and dropping the small load-bearing details.

Honest limits: the apps are not code signed or notarized yet, so Windows SmartScreen warns once and macOS Gatekeeper needs a right-click Open on first launch. Whisper is great but not perfect, especially on accents and crosstalk.

Both are MIT. macOS repo: https://github.com/v2matosevic/talkty-mac

Happy to go into the audio pipeline, the auto-paste focus dance on Windows, or the prompt design.
