# Changelog

User-facing changes, newest first. Dates are when the work landed.

## [Unreleased]

- Prompting can now decide before it spends: a fast check classifies your dictation first, so a genuinely one-line ask can skip the rewrite entirely instead of waiting one to three seconds for a model to hand back roughly the same sentence. Substantial requests start on the better model instead of escalating to it. Off by default; Settings value `PromptPlanning` has Off, Hints and Full. Details and limits: `docs/PROMPT-PLANNING.md`.
- Fixed: valid decision responses were being discarded. Probabilities are rounded by the provider, so a distribution over several options routinely misses 1.0 by more than the old window allowed; a peer measured 2.4% of 292 calls thrown away, skewed toward Croatian. The allowance now scales with the number of options, and one bad answer in a batch no longer discards the good ones alongside it.

- Optional prompt check for Prompting: after your dictation is rewritten into a coding-agent prompt, Talkty can compare the two and tell you when something you said is missing. Exact numbers, file names and function names are compared on your PC; the rest is judged by a small decision model over your existing OpenRouter key. It never rewrites your prompt and never delays copy or paste.
- Settings > Cloud & Prompting > Prompt check: Record only (default, shows nothing), Show concerns, or Off.
- Fixed: a prompt-check notice could be cut off mid-sentence when Talkty was in the tray, which is when you are most likely to see it. Notices now fit the Windows notification limit and quotes end on a whole word.
- Fixed: a prompt-check charge settled just after midnight was dropped from the local daily spend record instead of counting against it.
- Implemented and verified locally on Windows only. It is not in any installer or published release yet. Details and evidence: `docs/PROMPT-FIDELITY.md`.

## [1.3.4] - 2026-09-16

- MAI-Transcribe 2 sends smaller audio uploads using native Opus compression. Requests were 56–57% smaller than 1.3.3 on two test recordings, with identical transcripts. This reduces the amount to upload, particularly helpful on slower connections; it is not a claim of 56–57% faster transcription.
- Cloud connection and encoder warm-up now run together and begin when the model is ready, reducing avoidable preparation before short first dictations.
- Saved vocabulary hints and clean transcription are preserved. MP3/WAV fallback remains available, and other cloud models continue using MP3.
- Added detailed latency diagnostics for encoding, request preparation and the cloud request. Network and provider response times can still vary.
- Upgrading preserves settings, history, downloaded models and an existing CUDA pack.

## [1.3.3] - 2026-09-15

- New recommended cloud model: Microsoft MAI-Transcribe 2 via OpenRouter. It is the cheapest cloud option ($0.10 per hour of audio), fast, handles 60 languages and leaves out filler words like "um". It does not support Croatian or Serbian; pick another cloud model or local Whisper for those.
- GPT-4o Mini Transcribe is no longer the recommended cloud model, and Qwen3 ASR Flash is no longer labelled the cheapest.
- Cloud transcription is much faster. Recordings are uploaded as compressed MP3 instead of WAV, about a fifth of the size, with the same words in testing. A 2.4-minute recording went from 10 to 26 seconds down to about 3, and short dictations take roughly half as long. The connection also warms up while you are still speaking.
- Your vocabulary now reaches MAI-Transcribe 2 as spelling hints: the first 50 words, with the ones you added first. In testing, names like "Revori" and "Athena Agent" came out right with hints and wrong without.
- A cloud recording with no speech in it now says "No speech was detected" instead of showing an error.

## [1.3.2] - 2026-09-05

- Words added in Settings now reach local Whisper transcription in English, with personal terms prioritized over the starter vocabulary.
- Vocabulary edits apply to the next recording and survive idle model reloads. Hints stay short, with whole words preserved.
- Recording controls and history are easier to use at different window sizes; cancellation and microphone activity have clearer feedback.
- History saves stay in order, audio meter updates no longer queue behind a busy window, and a microphone test cannot overlap dictation.

## [1.3.1] - 2026-09-05

- Cancelled prompting no longer pastes the raw transcription. Pending auto-paste also respects cancellation.
- Repeated recordings release the previous microphone device and cannot mix in late audio from an older recording.
- Smaller initial recording buffer, with automatic growth for longer dictations.
- Muted recordings that contain digital silence skip transcription. Empty cleaned results leave the clipboard alone.
- Microphone flush failures and clipboard copy failures are visible. Failed clipboard preparation aborts paste, and history actions only show success when copying worked.

## [1.2.2] - 2026-07-02

- Text replacements are now editable in Settings > Vocabulary — one rule per
  line ("misheard => correct"), with a Reset to defaults. Previously the rules
  only lived in settings.json. Use this to re-add "cloud => Claude" if you
  relied on it.
- Punctuation cleanup no longer corrupts abbreviations: "e.g. the", "i.e.",
  "vs.", "etc." keep their periods.
- Cloud transcription retries once on transient errors (rate limit, gateway
  hiccups) instead of failing immediately.
- The early streamed clipboard copy now gets the same punctuation cleanup as
  the final text.
- "Open models folder" link on the Settings Model page.
- The empty-history hint now shows your actual hotkey instead of always Alt+Q.

## [1.2.1] - 2026-07-01

- Settings rebuilt with a left navigation — six clear sections (Model,
  Audio & Language, Vocabulary, Cloud & Prompting, Behavior, Hotkey) replace
  one long scroll. The window is now resizable.
- The OpenRouter API key is masked by default, with an eye toggle to reveal it.
- History entries show copy and delete buttons on hover, and a Clear action
  removes all history.
- Dark themed scrollbars and text boxes everywhere — no more white OS controls
  breaking the theme.
- Hover the recording pill to see the "esc cancels" hint (ESC-to-cancel existed
  but nothing ever told you).
- Onboarding fixed: step numbers were invisible (white on white), and it
  recommended a model that no longer exists. It now also mentions auto-paste,
  ESC, and the Prompting sparkle.
- Status colors unified across the main window, pill, and toasts (one green,
  one red, one purple).
- Hotkey-conflict errors show as an in-app notification instead of a light
  OS dialog box.

## [1.2.0] - 2026-07-01

- Talkty now frees the speech model's memory after 15 minutes of inactivity —
  a Large model no longer holds 1.5-3 GB of RAM around the clock while the app
  sits in the tray. The model reloads automatically while you speak, so the next
  recording works exactly as before. Toggle with "Free memory when idle" in
  Settings > Behavior (on by default).
- Faster startup: GPU detection no longer runs on the launch path (it could
  block the app for up to 5 seconds), and settings are read from disk once
  instead of twice.
- Much less disk activity: the log file stays open instead of being reopened
  for every line, and verbose debug logging is off in release builds (set
  TALKTY_DEBUG_LOG=1 to re-enable it when diagnosing an issue).
- Fixed: saving Settings silently reset custom text replacements to defaults on
  the next launch.

## [1.1.6] - 2026-07-01

- Transcription accuracy: removed the default "cloud"→"Claude" and "sequel"→"SQL"
  replacements — they rewrote legitimate speech ("AWS cloud" became "AWS Claude").
  Existing installs keep their saved rules; re-add them in Settings if you want them.
- Transcription accuracy: the English coding-vocabulary prompt is no longer applied
  when transcribing other languages (or with auto-detect) — it was biasing
  non-English decoding toward English.
- Transcription accuracy: re-enabled Whisper's temperature fallback so a decode
  stuck in a repetition loop recovers instead of producing garbage.
- Prompting: long dictations are no longer silently cut off — the output ceiling was
  raised 4x and a truncated result now escalates to the next model instead of
  shipping incomplete.
- Prompting: an invalid or out-of-credits OpenRouter key now fails fast with a clear
  notification instead of silently trying all four models (up to ~48s) and pasting
  raw text.
- You now get a notification when a transcription fails or Prompting falls back to
  the raw transcription — including as a tray balloon when the window is hidden.
- The recording pill now shows a purple "Prompting…" state while the AI expands your
  dictation (it used to sit on "..." with no explanation).
- Fixed the main window showing red "Model Not Loaded" while a model was actually
  loading — the amber "Loading Model…" state now shows correctly.

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
