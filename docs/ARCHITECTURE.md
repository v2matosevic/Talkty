# Talkty architecture

A tour of how the app is put together, for contributors. User-facing docs live in the
[README](../README.md); the Prompting feature has its own deep-dive in
[PROMPTING.md](./PROMPTING.md).

## The big picture

Talkty is a .NET 8 WPF tray app built on MVVM (CommunityToolkit.Mvvm). There is no DI
container — services are constructed and wired by hand in the `MainWindow` constructor
and passed into the ViewModels.

The core loop:

```
global hotkey (Win32 RegisterHotKey)
  → MainViewModel.ToggleListening()
  → AudioCaptureService records 16 kHz mono float samples (NAudio)
  → hotkey again: stop + flush NAudio's in-flight buffers (tail-loss fix)
  → TrimSilence → TranscriptionService.TranscribeAsync
  → TextPostProcessor (replacements, hallucination strip, punctuation)
  → optional PromptRefinementService (Prompting mode, via OpenRouter)
  → ClipboardService → optional AutoPasteService (Ctrl+V at the cursor)
```

While recording, a small always-on-top overlay pill (`OverlayWindow`, `WS_EX_NOACTIVATE`
so it never steals focus) shows a live waveform, a timer, and the Prompting toggle. It
positions at the bottom-center of the monitor the cursor is on; an opt-in setting anchors
it just below the focused app's text caret instead (`GetGUIThreadInfo`, visible-caret flag
required, mouse-position fallback).

## Engines

`TranscriptionService` is the engine manager. It owns one `ITranscriptionEngine` at a
time and swaps implementations based on the selected model profile:

| Engine | Backend | Notes |
|--------|---------|-------|
| `WhisperEngine` | Whisper.net (whisper.cpp) | The default. Runtime auto-detect: CUDA (if the pack is installed) → Vulkan (bundled) → CPU. |
| `SherpaOnnxEngine` | SherpaOnnx (SenseVoice) | Legacy — only reachable via old settings. |
| `OpenRouterEngine` | OpenRouter audio API | Opt-in cloud transcription. |

`WhisperEngine` builds a `WhisperProcessor` tuned for short dictation: greedy decoding
(`BestOf=1`), no context carry-over between recordings, temperature 0 with the standard
0.2 fallback increment (so a repetition loop can re-decode), physical-core thread count
capped at 8, and an off-the-load-path warmup so the UI shows "Ready" immediately.

The processor is built with the user's actual language and vocabulary up front
(`SetLanguageHint` / `SetVocabularyPrompt`, forwarded on engine creation) so the first
transcription after any load — including idle-unload reloads — never pays a rebuild.
The warmup task is tracked and cancel-awaited before the processor is disposed, rebuilt,
or used for a real transcription: `WhisperProcessor` wraps a single native state, so
concurrent `ProcessAsync` calls (or a dispose mid-processing) corrupt or throw. If a
wedged warmup outlives the bounded wait, the engine abandons the processor rather than
disposing it — a leaked model beats a lost dictation.

**Idle unload:** the app lives in the tray, and a loaded model holds hundreds of MB to
multiple GB of RAM. After 15 minutes without a transcription, `TranscriptionService`
disposes the engine and reclaims that memory. The reload is transparent: it is kicked
off the moment the next recording *starts* (so it overlaps with the user speaking), and
`TranscribeAsync` awaits any in-flight reload through the shared load semaphore. Cloud
profiles are never unloaded (nothing local to free).

## Model profiles

`ModelProfile` is an enum persisted in `settings.json` **by number** — never reorder or
remove members, or every user's saved model silently remaps. To retire a model, remove
it from the Settings catalog only; `SettingsViewModel.RemapRetiredProfile` bumps users
still on a retired value to the closest kept one.

Models download on demand from HuggingFace (`ModelDownloadService`: HTTP range resume,
retries with backoff) into `%AppData%\Talkty\Models\`.

## The CUDA pack

The installer ships the Vulkan backend only (it accelerates NVIDIA, AMD and Intel GPUs);
the CUDA runtime — ~578 MB uncompressed, most of it `cublasLt` — is an optional in-app
download handled by `CudaPackService`. Settings > Behavior offers it when GPU mode is on,
an NVIDIA driver is present (nvidia-smi/nvml in System32), and the pack files are missing.

Design points worth keeping intact:

- **One manifest.** `CudaPackService.RequiredFiles` is the single list of CUDA files
  (relative to the app root). `WhisperEngine.CheckCudaAvailability` checks the same list,
  so "installed" and "loadable" can never disagree. The csproj `CopyCudaDlls` targets and
  `installer/make-cuda-pack.ps1` mirror it — sync all of them on a CUDA version bump.
- **Never a truncated DLL.** The zip downloads and extracts into `.cuda-pack-tmp` next to
  the exe (same volume), and files land via `File.Move` — a rename. A crash mid-install
  leaves a file *absent* (the offer reappears and repairs) but never *truncated*, which
  would pass the existence checks and select the CUDA runtime "no fallback".
- **Resumable.** Download retries with exponential backoff and resumes via HTTP Range;
  a cancelled download keeps its partial zip and continues next time. A process-wide gate
  serializes installs, and the Settings window cancels its download on close.
- **Restart to activate.** Whisper.net picks its native runtime at first load and can't
  swap in-process; `App.Restart()` relaunches with `--restarted`, which makes the new
  instance wait (up to 10 s) on the single-instance mutex instead of exiting while the
  old instance finishes disposing a multi-GB model.

The pack zip is built by `installer/make-cuda-pack.ps1` and uploaded once to the
version-independent release tag `cuda-pack-cu13`.

## Vocabulary: the two-layer system

1. **Prompt biasing** — a deliberately short natural sentence fed to Whisper's
   `initial_prompt`. Short because Whisper regurgitates prompt content during silence;
   every extra term is a hallucination risk. Applied only when the language is English
   (an English prompt degrades non-English decoding).
2. **Deterministic replacements** — case-insensitive, word-boundary regex replacements
   applied *after* transcription ("cube cuddle" → `kubectl`). These cannot hallucinate,
   so anything that can live here instead of the prompt, does. Keys must never be real
   English words — that rewrites legitimate speech.

`TextPostProcessor` also strips non-speech tokens (`[MUSIC]`, `♪`) anywhere and
YouTube-closing hallucinations ("Thanks for watching") only at the very end of the
transcript, joins sentence fragments split by pauses, and normalizes punctuation.
It is the most test-covered class in the repo — edge cases live in `Talkty.Tests`.

## Auto-paste

`AutoPasteService` wraps the Win32 focus dance. The essentials:

- The paste target is captured at recording **stop** time, so you can switch apps while
  speaking.
- Foreground privilege is claimed on the UI thread immediately on the hotkey (Windows
  only grants `SetForegroundWindow` to the thread that last received input).
- The overlay is non-activating, so in the common case the target never lost focus and
  no focus manipulation happens at all. Focus restoration (`AttachThreadInput` +
  `SetForegroundWindow`) only runs if the user actually switched apps.
- Modifier keys are explicitly flushed before the synthetic Ctrl+V, and the clipboard is
  re-set right before the paste.
- Every paste returns a `PasteOutcome`; anything but `Pasted` surfaces as a toast telling
  the user the text is on the clipboard and why. Elevated targets are detected via the
  process token (`TokenElevation`) — Windows UIPI silently drops keystrokes sent to an
  admin window from a non-elevated process, which used to look like a successful paste.
- Opt-in clipboard restore: the pre-recording clipboard text is snapshotted before
  anything overwrites it and put back shortly after a successful paste — but only if the
  clipboard still holds the transcription, so a rapid follow-up dictation is never
  clobbered by a stale restore.

## Prompting

Opt-in per recording via the sparkle on the overlay pill. `PromptRefinementService`
sends the transcription to OpenRouter chat completions with a "reformat, don't
summarize" system prompt and a model fallback chain. Guards: a completeness check
escalates to the next model when a result looks summarized, `finish_reason == "length"`
(truncation) is treated as a failure, and key-level errors (401/402) abort the chain
with a user-facing toast. Full rationale: [PROMPTING.md](./PROMPTING.md).

The OpenRouter API key is stored encrypted with Windows DPAPI (`ApiKeyProtector`) and
never touches disk in plain text. It powers both cloud transcription and Prompting.

## Settings & persistence

`SettingsService` persists `settings.json` and `history.json` under `%AppData%\Talkty\`
with atomic writes (temp file + move). History writes are serialized with a semaphore.

One footgun worth knowing: `MainViewModel.ApplySettings` copies each field from the
dialog's `AppSettings` onto the persisted instance explicitly. **Any new `AppSettings`
field must be added there too**, or it will apply live but silently reset on restart.
Similarly, `SettingsViewModel.SaveSettings` builds a fresh `AppSettings` — fields
without UI must be carried through explicitly.

## Versioning & release

`version.txt` at the repo root is the single source of truth: `Directory.Build.props`
reads it into `<Version>`, and the Inno Setup script reads it at preprocess time.
`version.json` (release notes + download URL for the in-app update checker) is the one
remaining manual edit per release.

```
dotnet publish -c Release -r win-x64 --self-contained true   # NOT single-file
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\TalktySetup.iss
```

Single-file publish is off deliberately — Whisper.net's native DLLs must live in the
`runtimes/` folder structure. Always publish before running ISCC: the installer's
`[Files]` glob matches the publish folder, and an empty one fails the compile.
Delete the publish folder before a release publish — `dotnet publish` does not clean
it, and stale files (e.g. CUDA DLLs from an old fat build) would ride into the installer.

**CUDA is not bundled** (since v1.3.0): the installer ships the Vulkan backend (works on
NVIDIA/AMD/Intel); `CudaPackService` downloads the optional CUDA runtime in-app from the
`cuda-pack-cu13` GitHub release (asset built by `installer/make-cuda-pack.ps1`). A fat
build remains available with `-p:BundleCuda=true` (requires CUDA Toolkit v13.1).

## Logging

`Log` (in `LoggingService.cs`) writes to `%AppData%\Talkty\Logs\` through a single
persistent auto-flushing writer — durable per line, so a hard native crash still leaves
a full tail. Release builds log Info and above; set the `TALKTY_DEBUG_LOG=1` environment
variable to capture Debug detail when diagnosing an issue. Crash reports are written
separately next to the executable.
