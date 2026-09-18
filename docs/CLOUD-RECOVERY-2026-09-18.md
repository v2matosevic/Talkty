# Cloud recording recovery and fallback, 18 September 2026

## Incident and scope

The installed app log `talkty_2026-09-16_15-08-15.log` records two HTTP 429 responses for an 8.8-second recording at 18:19:56–18:19:58 on September 18. The app hid the overlay, reset its status, and saved no history entry. Audio was only in memory and replaced by the next recording. Other matching failures occurred at 18:17, 18:19, and 18:21.

Marko requested recovery, a selectable cloud fallback, and optimization of both MAI and Qwen. He accepted Qwen Flash as the backup. No public release was requested.

## Implemented behavior

- Cloud audio is saved atomically before the API call, encrypted with Windows DPAPI for the current user. Files live in `%APPDATA%/Talkty/Recovery`. Each recording has its own ID, original timestamp, language, and Prompting choice.
- Failed recordings remain in the main window with a persistent explanation and Retry/Discard controls, survive restart, and are not overwritten by later recordings. Successful takes are removed from recovery only after history persistence succeeds. Disk failure stops upload and retains the in-memory copy with an explicit warning.
- Retry uses the currently selected primary model and original language/Prompting choice. It copies text if clipboard output is enabled, but never auto-pastes into the window from an old recording. Local models can also retry saved audio.
- Settings > Cloud & Prompting contains a backup picker with Off and every supported cloud profile. Qwen3 ASR Flash is the default; existing primary choices remain unchanged. Added Qwen3 ASR 1.7B as a separate, append-only enum value (17), preserving older numeric settings.
- Temporary failures (408, 429, 5xx, connection errors, timeout, malformed response) can use the backup. Authentication, balance, invalid request, no speech, and cancellation do not trigger it. Identical models and unsupported explicit languages are skipped.
- If a compatible backup exists, the primary does not sleep and retry the same busy provider. The final provider gets at most one transient retry, honoring Retry-After up to five seconds. Longer cooldowns are surfaced without waiting. The next recording still starts with the selected primary.
- Encoded audio can be reused within one attempt chain when both models use the same upload format. Caches never cross recording boundaries.

## Model choice and measured optimization

Sources checked September 18:

- [MAI-Transcribe 2](https://openrouter.ai/microsoft/mai-transcribe-2): $0.10/audio hour, Azure.
- [Qwen3 ASR Flash](https://openrouter.ai/qwen/qwen3-asr-flash-2026-02-10): $0.000035/second, about $0.126/hour, Alibaba.
- [Qwen3 ASR 1.7B](https://openrouter.ai/qwen/qwen3-asr-1.7b): $0.000008/second, about $0.0288/hour, DeepInfra.
- [Whisper Large V3 Turbo](https://openrouter.ai/openai/whisper-large-v3-turbo): DeepInfra and Groq, different prices; listed from $0.000003/second.
- [OpenRouter STT contract](https://openrouter.ai/docs/guides/overview/multimodal/stt): provider options are provider-specific; chat routing controls do not apply. Do not add ignored routing hints as a supposed optimization.
- [Alibaba Qwen API](https://help.aliyun.com/en/model-studio/qwen-asr-api-reference): vocabulary context uses a system message in its native chat protocol. An equivalent working mapping through OpenRouter STT has not been established, so no unverified context field is shipped. MAI retains its verified 50-term phrase list; deterministic replacement rules still apply to both models.

Paid checks used only the existing synthetic 15.8-second fixture at `%TEMP%/Talkty-latency-20260916/short.wav`, never a user's microphone recording. Each row is one request, not a statistical benchmark:

| Model / upload | Elapsed | Result |
| --- | ---: | --- |
| Qwen 1.7B / 48 kbps MP3 | 60.0 s | Timed out; rejected as default backup. |
| Whisper Turbo / 48 kbps MP3 | 3.010 s | Complete ending and numbers, but “Playwright” became “play right”; fragmented punctuation. |
| Qwen Flash / 48 kbps MP3 | 3.473 s | Preserved C++, PostgreSQL, Kubernetes, Playwright, numbers and ending; “Velora” became “Velero”. Accepted backup. |
| Qwen Flash / 24 kbps Opus experiment | 2.149 s | Smaller/faster in this sample, but C++ became C. Rejected; final code retains MP3 for Qwen. |
| MAI / 24 kbps Opus + five vocabulary terms | 1.233 s | Preserved every fixture term, number, correction and ending. Retained existing codec and options. |

MAI's compact JSON, native Opus, language hint, vocabulary and connection warm-up remain. Qwen keeps MP3 to avoid the observed quality regression. No broad accuracy equivalence or guaranteed latency reduction is claimed. Immediate failover removes the prior one-second retry delay plus an unnecessary request when the primary returns a transient failure and a usable backup exists.

## Verification

Release build: zero warnings/errors. Release test suite: 143 passing tests. New coverage exercises the real engine/service with mocked HTTP, both directions of MAI/Qwen failover, cancellation, non-transient errors, disabled/duplicate/unsupported backups, recording-scoped upload caching, encrypted recovery reload/delete, disk/history failures, successive failed takes, retry without auto-paste, and Settings save/apply/JSON compatibility.

Production WPF main-window XAML rendered at 380×420 and 420×520 with a saved recording. Real Settings window rendered at 680×560 using isolated fake settings/audio. Images inspected in `Talkty.Tests/bin/ui-evidence/`; live desktop input was not simulated. Existing regular-history layout checks still pass.

Reported usage cost for the four successful live checks: **$0.001547**. The Qwen 1.7B timeout returned no usage amount, so its billed cost is unknown (listed-price estimate for that clip: about $0.000128). No further paid requests were made.

## Local delivery

Installed at `B:/Talkty`, version `1.3.4-recovery.20260918`, from `Talkty.App/bin/Release/recovery-20260918`. Four changed files were SHA-256 verified: exe, dll, pdb, deps.json. Manifest: `installer/output/recovery-installed-manifest.json`. Previous files: `installer/output/pre-recovery-20260918`. Settings/history hashes were unchanged during replacement. No installer or Windows uninstall registration update was performed; this is a local patch build.

The old process was idle (last overlay-hide at 18:49:58) before replacement. Its DLL briefly remained locked after process exit; replacement was retried after the lock cleared, all four hashes matched, and the new app launched as PID 49452. Startup log `talkty_2026-09-18_18-52-55.log` confirms Alt+Q registered, LargeTurbo loaded on CUDA at 18:53:01, and warm-up completed at 18:53:03. Marko had switched his primary to local LargeTurbo during the task; this choice was preserved. Cloud fallback becomes active when a cloud primary is selected.

Public `version.txt` and `version.json` remain unchanged. Nothing was pushed or publicly released. The earlier lost audio cannot be recovered retroactively.
