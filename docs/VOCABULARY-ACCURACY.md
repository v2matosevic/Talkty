# Vocabulary accuracy fix, 2026-09-05

## Problem and behavior

CustomVocabulary was editable and persisted, but MainViewModel passed only a fixed
coding sentence to Whisper at startup and on every recording. User additions never
reached the recognizer. Text replacements were a separate working feature.

VocabularyPromptBuilder now uses the saved list. Additions outside the default catalog
come first, followed by the existing high-value default terms still present in the list,
then the remaining selected defaults. Ordering within each group follows Settings.
Whitespace is normalized, duplicate spellings are compared case-insensitively, and
whole terms are selected within a conservative 200 UTF-8 byte budget. This stays below
Whisper's 224-token prompt window without guessing tokens from character counts.
Oversized terms are skipped rather than truncated. The full saved list is preserved.

The same builder supplies startup and recording prompts. ApplySettings refreshes the
model reload hint as well. WhisperEngine tracks the pending hint separately from the
prompt used to build the current processor, so changing settings cannot falsely mark
an old processor as already updated. The next recording rebuilds it when necessary.

English, explicitly selected, and local Whisper remain the supported hint path.
Auto-detection and other languages receive no English prompt. Cloud and SenseVoice
do not consume these hints. Text replacements retain their existing behavior.
The model selection, GPU preference, microphone processing and decoding settings are
unchanged. No full vocabulary catalog is forced into every recording.

## Verification

`dotnet test Talkty.Tests/Talkty.Tests.csproj --no-restore --verbosity minimal`
passed 99 tests in the shared checkout, including 16 new vocabulary cases. Coverage
includes appended custom names beating presets, preserving spelling, deduplication,
UTF-8 budgeting, oversized entries, language/engine gating, and the real MainViewModel
passing saved terms at startup and recording time. Settings edits, disabling hints,
and language changes are checked through that recording pipeline.

Confidence is high that saved terms now reach the local recognition pipeline. These
tests do not measure recognition accuracy on Marko's voice. A before/after comparison
requires the same representative recordings and manually corrected reference text.
Evaluate name errors, ordinary-word errors, invented words during silence, and latency
before deciding whether full Large v3 improves enough to replace Turbo.

Provider references checked during the assessment:

- https://console.groq.com/docs/speech-to-text (Whisper prompt spelling guidance and 224-token limit).
- https://huggingface.co/openai/whisper-large-v3-turbo (Turbo speed/accuracy tradeoff).

## Local delivery

Version 1.3.2 was installed at `B:/Talkty` using the elevated Inno installer on
2026-09-05, exit code 0. It includes the independently verified desktop UI work
documented in `UX-PERFORMANCE-2026-09.md`. The vocabulary code commit is `32f922c`.

All 488 installer payload files match the published build by SHA-256 (the `.pdb`
is intentionally excluded by the installer). Settings excluding launch hints match
the pre-install fingerprint; the existing CUDA pack remains present. History changed
while Marko continued dictating during packaging, so its earlier fingerprint cannot
prove byte-for-byte preservation. The new app loaded 50 existing history entries.

Startup at 00:39 local time verified version 1.3.2, LargeTurbo on CUDA, Alt+Q registered,
and completed processor warmup. Invoking the installed assembly's prompt builder on
the actual saved vocabulary verified all six user additions are included in the
200-byte hint. No microphone recordings were generated or sent to a cloud service
for this verification.

Installer, manifest, fingerprint results and logs are in ignored `installer/output/`.
Public release metadata remains at 1.3.0; no public release was cut.
