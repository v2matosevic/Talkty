# Talkty 1.3.3 (installed locally, not public yet)

Built and installed on this machine on 15 and 16 September 2026. Marko asked for MAI-Transcribe 2 to become the main cloud model, then reported cloud transcription as slow and ignoring his vocabulary. Both reports were real and are fixed in this build. Nothing is on GitHub yet: `version.json` still advertises 1.3.2, so existing installs are not notified.

## What changed

- MAI-Transcribe 2 (`microsoft/mai-transcribe-2`) is a new cloud model and the recommended one, first in the Cloud list. OpenRouter lists it at $0.10 per hour of audio, the cheapest cloud option; Qwen3 ASR Flash lists $0.000035 per second, which is $0.126 per hour. Microsoft's documentation lists 60 languages (checked 15 September 2026). Croatian and Serbian are not among them, and the picker says so.
- **Speed.** Uploading the audio was the bottleneck, not the model. Recordings now go to every cloud model as 48 kbps MP3, encoded by Windows' built-in Media Foundation encoder, at about a fifth of the WAV size. WAV stays the fallback on Windows editions without Media Foundation. At recording start, Talkty now opens the HTTPS connection and starts the encoder while the user speaks, and it keeps the connection open for 10 minutes instead of 1.
- **Vocabulary.** MAI-Transcribe 2 now receives the saved vocabulary as keyword hints (`phraseList`): the first 50 words, with the user's own additions first. Azure rejects a 51st phrase. Previously no cloud model received the vocabulary; only the text-replacement rules applied after transcription.
- MAI requests ask for the "clean" transcript style, which leaves out filler words.
- A well-formed empty transcript reads "No speech was detected. Nothing was copied.", like local Whisper, instead of a cloud error.
- Commits: `ea17455` (model), `1df3b1f` (recommended + empty-transcript handling), `1dc462d` (MP3, prewarm, vocabulary), plus this documentation commit.

## Speed measurements

All figures were measured on this machine against OpenRouter on 15 and 16 September 2026. The upload link varied a lot during testing, so the before and after figures were taken side by side.

| Recording | WAV (before) | MP3 (now) |
|---|---|---|
| 11 s clip, direct requests | 1.0 to 2.7 s | 0.45 to 1.1 s, identical text |
| 144 s clip, app engine | 10.3 s (first run) | 2.9 s |
| 144 s clip, direct requests at the same moment | 14 to 26 s | 3 to 5 s, 2 of 302 words differ ("images" correct where WAV said "image"; "playwright" lower-cased) |

Upload sizes: the 11 s clip dropped from 355,886 to 67,823 bytes, and the 144 s clip from 4,619,566 to 867,455 bytes.

Formats compared: FLAC (half the size, 12 to 16 s for the long clip), MP3 at 24, 32 and 48 kbps, and Opus at 24 kbps. Opus was smallest with identical words but needs an extra library. MP3 at 24 and 32 kbps started changing words. 48 kbps MP3 was chosen because it needs no new dependency. A warmed connection or HTTP/2 alone did not change request time measurably.

Before the fix, Marko's real dictations of 2.0, 8.5, 12.6 and 17.8 s took 0.5, 1.4, 3.2 and 2.5 s. Real dictations after the fix are not yet measured.

Splitting long recordings into parallel requests was considered and rejected. Compression already captured most of the gain for typical dictations under 20 s, and chunk boundaries risk broken words and false sentence breaks.

## Vocabulary measurements

A synthesized clip with Marko's own terms, sent to MAI-Transcribe 2:

- Without hints: "Athena agent", "Revora", "DeepSeek", "version 2.0.0.0.hr".
- With the 50 terms the app builds from his saved vocabulary: Athena Agent, Revori, Kenshi, Deepseek, Claude Code and Claude in Chrome all came out as saved, and "version 2.hr".
- "OpenRouter" and "version2.hr" are not in his vocabulary; the clip gave "open router" and "version 2.hr". Adding them in Settings > Vocabulary, or as a text replacement, is his call.
- The phrase list accepts 50 terms; 51 or more returns a provider 400. Terms with dots, slashes and `#` are accepted.

## Installer

- `TalktySetup-1.3.3.exe`, 60,469,647 bytes.
- SHA-256: `6cb7b29020c268214eb26b90f9c4ff25f0fbf0e4724ccf547f95fe8c9357c95e`.
- Built with `/DMyAppSourcePath` from `Talkty.App/bin/Release/verified-1.3.3-20260915`, a fresh self-contained publish of `1dc462d` from a clean tree for app sources: 489 files, no CUDA DLLs, no linux/arm64/x86 runtimes. The exe reports `1.3.3+1dc462d`.
- Payload manifest: `installer/output/payload-1.3.3.csv` (488 files; the installer excludes the PDB). Install log: `installer/output/install-1.3.3-final.log`.
- The two earlier 1.3.3 builds were renamed, not deleted: `verified-1.3.3-20260915-precommit`, `verified-1.3.3-20260915-first-install`, `TalktySetup-1.3.3-first-install.exe` and `payload-1.3.3-first-install.csv`. They are superseded.

## Verification

| Check | Result |
|---|---|
| Unit tests | 120 pass in Release: the 99 from 1.3.2 plus 21 new. They cover the enum number, payloads (clean style and phrase list for MAI only, the 50-term cap, the upload format), MP3-or-WAV encoding, empty versus malformed responses, cloud-term ordering and language independence, and the recording flow sending terms and prewarming the cloud. |
| MP3 on every cloud model | All six cloud models accepted MP3 uploads (HTTP 200). |
| App engine, live | Through `OpenRouterEngine`: the vocabulary clip with 50 hints (1.8 s in a fresh process), the 144 s clip as MP3 (2.9 s), and the prewarm step (0.35 to 0.6 s, during speech). A quiet clip gives "No speech was detected". |
| Install | Final elevated Inno upgrade, exit 0. HKLM entry "Talkty 1.3.3". All 488 installed files match the manifest. settings.json and history.json were unchanged by the install. The 5 CUDA pack files were kept. |
| Relaunch | Talkty started on MAI-Transcribe 2 ("Cloud model ready"), Alt+Q registered, and the update check ran on 1.3.3. |
| Real use | On the first 1.3.3 build, MAI-Transcribe 2 was selected in Settings (`modelProfile` 16), and four dictations went through it and were pasted. |

## Test spend

The live tests used Marko's saved OpenRouter key. About 53 minutes of audio went to MAI-Transcribe 2, costing about $0.09; that is the MAI charge visible on his OpenRouter dashboard. Most of it was the 144 s clip, sent about 18 times while comparing formats. Ten short clips sent to the other cloud models cost under one cent. His own dictations were about a tenth of a cent.

## Limits

- Croatian and Serbian are not supported by MAI-Transcribe 2. A pinned `hr` is accepted without error, but recognition of Croatian speech is untested; this machine has no Croatian voice to synthesize one.
- Filler removal and vocabulary hints were tested on synthesized speech only, not on natural dictation.
- Real dictation speed after the fix is not yet measured.
- The WAV fallback for Windows without Media Foundation is covered by a unit test that accepts either path. It was not run on such a machine.
- The Settings window was not rendered for this release; the picker was used live on the first 1.3.3 build.

## Remaining release steps

1. Push `ea17455`, `1df3b1f`, `1dc462d` and the documentation commit to `main`. The coordination push review could not attribute the commits to a live session, so the push waits for Marko.
2. Create GitHub release `v1.3.3` with `TalktySetup-1.3.3.exe`, and verify the uploaded asset's digest against the SHA-256 above.
3. Only after the installer is public, bump `version.json` to 1.3.3. The app reads it from GitHub's `main`, and every existing install will be notified.
