# MAI latency optimization, 16 September 2026

Marko requested implementation after reporting inconsistent waits on both short and long dictations.

## Changes

- MAI now uses native Opus at 24 kbps, mono 16 kHz. MP3 remains the fallback and the format for other cloud models; WAV remains the fallback when Media Foundation is missing.
- Added Concentus.Oggfile 1.0.7 and Concentus.Native.NetCore 1.5.2. Native encoding is required for the Opus path: the managed codec was too slow in the comparison. The packaged `opus.dll` is loaded from the assembly directory, including in the verification harness. Native library lifetime is the process lifetime.
- JSON is serialized directly to UTF-8 with no unnecessary escaping of base64 plus signs. Vocabulary hints and clean transcription remain in the same JSON provider options.
- Encoder initialization and HTTPS warm-up run concurrently, once-per-process for the encoder. Warm-up also starts when a cloud model becomes ready, covering short first dictations. Concurrent warm-up callers await the same task. The key response is fully read so its connection can return to the pool.
- Monotonic timings now log encoding, payload preparation, request body bytes, body-written time, response headers, response reading, attempt and HTTP status. No new audio/transcript logging was added.
- Opus encoding checks cancellation between one-second blocks. Tests cover non-frame-aligned audio, decoding and the end of the recording, cancellation, JSON byte fidelity and the timed HTTP content.

## What the evidence supports

The running 1.3.3 logs showed an 11.5-second dictation taking 2,409 ms and a 25-second dictation taking 1,039 ms. Their MP3 preparation took 19 and 45 ms respectively. These timings include neither proof of slow upload nor proof of provider queueing: the old code measured the whole request together.

Two synthetic English fixtures were checked with vocabulary terms, technical names, numbers and an explicit final sentence. All successful MP3 and Opus requests returned identical text within each fixture. This is bounded evidence, not a guarantee for accents, noisy microphones or every language.

| Fixture | Previous MP3 JSON | Native Opus JSON | Reduction |
|---|---:|---:|---:|
| 15.8 seconds | 135,622 bytes | 59,124 bytes | 56.4% |
| approximately 33 seconds | 280,477 bytes | 121,404 bytes | 56.7% |

The first managed Opus experiment took 608 ms to encode the short fixture, compared with 78 ms for MP3. It was rejected as a production path. With native Opus loaded, cold encoding was 59 ms; the final published-layout engine, after warm-up, encoded it in 32 ms.

Live request times on the short fixture overlapped between formats; there is no demonstrated large latency gain on the uncongested link. On the longer fixture, one sequential baseline/Opus pair took 926/846 ms for HTTP, with encoding taking 92/81 ms. One pair does not establish a statistically reliable speed improvement or lower tail latency.

The final self-contained build's production engine returned the short fixture in **844 ms**: 32 ms encoding, 32 ms payload preparation, 774 ms HTTP. Body-written was 2 ms, response headers 773 ms, response reading 1 ms. This demonstrates that local preparation was a small part of this request.

`bodyWritten` means handed to the HTTP transport, not acknowledged by the provider. Kernel/socket buffering means the remaining wait includes network transfer, OpenRouter forwarding, provider waiting and inference. It must not be labelled pure inference or server queue time. Smaller uploads reduce sensitivity to a slow uplink, but do not remove provider/network variability.

## Alternatives investigated

- OpenRouter documents multipart uploads, but the .NET probes returned HTTP 400 `Invalid multipart/form-data body`, with both quoted and unquoted boundaries. The JSON invalid-provider-option control returned a provider 400 as expected. Multipart provider-option preservation was therefore **not established**. Keep JSON rather than risk losing vocabulary or clean style.
- Splitting speech into requests while recording could overlap inference with speech, but changes recognition context and introduces boundary/merge failures. It was not implemented in this patch. These measurements do not establish a safe way to promise instant or consistent completion with the same batch model.
- No direct-Azure comparison was performed. No provider/model switch or duplicate racing requests were introduced.

## Verification and reproducibility

- Final source: `dotnet build -c Release --no-restore`, zero warnings/errors; `dotnet test -c Release --no-build --verbosity minimal`, **124 passed**.
- Self-contained publish: `installer/output/latency-20260916-final`, informational version `1.3.3-latency.20260916` plus source suffix; built into a new directory. Includes native `opus.dll`, Concentus and the Ogg container library.
- Production engine check: `tools/cloud-engine-check.ps1`, published layout, 15.8-second fixture, five hints, warm-up; success at 844 ms with the expected complete transcript.
- Bounded comparison script: `tools/cloud-latency-check.ps1`. Alternates order across rounds, reports exact response costs, and limits audio seconds before sending. Native Opus and old MP3 do not always round to the same billed second.
- Machine-local synthesized fixtures and raw results: `%TEMP%/Talkty-latency-20260916`. No microphone recordings were replayed. Consolidated results are in `docs/cloud-latency-results-2026-09-16.json`.

## Delivery

Installed locally at `B:\Talkty`. Eight changed/new files matched the published layout by SHA-256; manifest: `installer/output/latency-installed-manifest.json`. The previous files are backed up in `installer/output/pre-latency-20260916`. Settings and history hashes were unchanged during replacement. The old app was idle before restart; the new process launched as PID 30124.

Relaunch log `talkty_2026-09-16_14-06-27.log` confirms MAI ready and Alt+Q registered. Process module inspection confirms `B:\Talkty\opus.dll` and both Concentus assemblies loaded. The final packaging-only change makes the third-party license notice part of future publishes; republishing left all eight installed manifest hashes identical.

Follow-up verification at 14:31: all eight installed hashes still matched. Three real dictations at 14:30 reached paste in 1,246 / 623 / 568 ms; their engine times were 1,070 / 543 / 504 ms. All uploaded Opus with 50 vocabulary hints and succeeded on the first HTTP attempt. Three observations confirm real use, not a reliable before/after speed distribution. Marko then said it seemed really good and explicitly authorized publishing the release.

The initial installation was a local performance build with version 1.3.3. The authorized public release carrying these changes is 1.3.4; see `docs/RELEASE-1.3.4.md` for its delivery evidence.

Total reported cost from successful comparison requests plus the final production-engine check: **$0.007639**, under the announced one-cent budget. Four invalid multipart/control probes returned HTTP 400 without usage costs. Approximately 275 billable seconds were reported by successful calls; no additional paid calls are planned for this patch.

## References

- [OpenRouter speech-to-text contract](https://openrouter.ai/docs/guides/overview/multimodal/stt), inspected in a browser on 16 September 2026.
- [Microsoft MAI API](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/mai-transcribe), inspected in a browser on 16 September 2026.
- [Concentus](https://github.com/lostromb/concentus), [Ogg container library](https://github.com/lostromb/concentus.oggfile), native package license bundled in `THIRD_PARTY_LICENSES`.
