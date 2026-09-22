# Talkty 1.4.0

Local Windows installation and open-source release preparation requested by Marko on
22 September 2026. Installation and draft preparation are in progress. Public Latest
remains 1.3.5 until publication is separately requested.

## Release notes

- Transcription can start during pauses while preserving the full recording and selected
  model. Early text is used only when the final flushed audio matches exactly; otherwise
  the normal full pass runs. Disable this under Settings > Behavior. Cloud can bill one
  extra pass, including cancelled takes. Nothing is pasted provisionally.
- CUDA uses Flash Attention. On one RTX 3060 Ti and two synthetic English fixtures,
  median decoding was 13–24% faster with identical transcripts. Paused local takes can
  finish almost immediately after Stop; cloud results varied. These are bounded findings,
  not a guaranteed speedup for every recording. [Measurements](TRANSCRIPTION-LATENCY-2026-09-22.md).
- Corrected MAI's clean-style request options, which caused provider HTTP 400 errors.
  Native Opus uploads, full context, vocabulary hints and clean output remain enabled.
- Restored the opt-in Prompting control in Settings > Cloud & Prompting. The earlier
  pill cleanup removed its button but had not supplied a usable replacement. The
  recording now uses the saved setting. Prompting adds a paid text request and delay;
  it stays off by default, and command mode bypasses it.
- Includes optional [prompt fidelity checks](PROMPT-FIDELITY.md) and advanced
  [prompt planning](PROMPT-PLANNING.md). They only apply to Prompting. Planning defaults
  off; fidelity's default Record only mode can make a bounded text request after a
  generated prompt is delivered.
- Includes opt-in [command mode](COMMAND-MODE.md), with a separate shortcut, provisional
  local recognition, service discovery, captured target window, progress and result on
  the pill. It requires an external local service and does not paste commands into apps.
- Removed the blocking command-preview shutdown wait. Native decoder work is serialized,
  cancellation unwinds before reuse, and shutdown does not block the dispatcher.
- Updated installer privacy text and source-build instructions. The checked installer
  script now uses a fresh non-single-file publish, verifies native payloads, and produces
  a file manifest and SHA-256 checksum. CUDA remains an optional separate pack.

- Fixed the uninstaller's Keep data choice: deletion now checks the actual answer.
  The old code asked but did not condition its deletion entry. Keeping data is the
  default, including when confirmation messages are suppressed. An isolated installer
  test proved both Keep and Remove against repository fixtures, with all four process
  exits 0. User data removal runs in the uninstall event, not a Setup-time Check entry.
  The existing Windows installation is upgraded in place; its uninstaller is not run.

Upgrading preserves settings, history, encrypted recovery files, downloaded models and
the optional CUDA pack. Recognition language and primary model are not changed.

## Verification and packaging

Behavioral baseline: `f6a8f53`, 387 passing tests and GitHub CI 35758274073.
The 1.4.0 Release build and **389 tests passed locally**, including saved Prompting
activation, command isolation and off-screen Settings rendering. Six pre-existing
test-source warnings remain. Exact-source CI will be recorded below. Earlier paid model
measurements are reused because this packaging/control fix does not change the engines,
their request payloads or audio processing. No further paid verification is required.

Package source, installer hash, manifest count, installation log, preserved-data checks,
startup verification and GitHub draft link will be recorded after those actions complete.
Interactive microphone/desktop testing and a clean-machine uninstall are not claimed by
the headless checks.
