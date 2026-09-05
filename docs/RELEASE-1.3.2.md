# Talkty 1.3.2 public release

Published 5 September 2026 at Marko's explicit request to finish the GitHub push and public release. This record supersedes the earlier pending-approval and local-only release notes.

- Public release: https://github.com/v2matosevic/Talkty/releases/tag/v1.3.2
- Release tag source: `170b02a89dc85355b068c8ef2138f764498bc928`.
- All seven previously local commits were reviewed and pushed to `main`. Coordination review reported unknown authors, with no WIP or live owners; Marko explicitly authorized publishing that exact previously reported scope.
- Source CI passed: https://github.com/v2matosevic/Talkty/actions/runs/33974694405
- Installer: `TalktySetup-1.3.2.exe`, 60,416,189 bytes.
- SHA-256: `2a54025499ce41f20702303f41394564973fb65c0bbb4f2e3acdc419fd69c2f8`.
- GitHub's uploaded asset digest and a fresh download both match the local installer.
- `version.json` was updated only after the installer became publicly available. The app reads this file from GitHub's `main` branch.

## Verification and artifact provenance

The existing verified installer was reused. Its file/product version is 1.3.2. All 489 files in `Talkty.App/bin/Release/verified-1.3.2-20260905/` match the saved packaging manifest. This includes the PDB excluded by the installer; the installed payload contains 488 files.

The portable PDB checksums match all 64 tracked source documents it records. `Resources/Styles.xaml` has no separate PDB document, so the complete compiled WPF resource bundle was independently compared against the newly rebuilt current source and matches exactly. The packaged app DLL also matches the app installed at `B:/Talkty`.

The current Release test run passed 99 tests. GitHub independently restored, built, and tested the pushed release source successfully. Historical installation evidence, desktop renders, and their limits remain in [the desktop report](UX-PERFORMANCE-2026-09.md) and [the vocabulary report](VOCABULARY-ACCURACY.md).

The repository is public and GitHub recognizes its MIT license. README/build instructions, contribution guidance, security policy, third-party notices, issue templates, and a pull-request template are present on GitHub. The optional CUDA runtime asset remains published under `cuda-pack-cu13`.

No installed app restart, settings change, microphone recording, or desktop input injection was needed for publication. Recognition accuracy on a reference corpus and mixed-monitor native interaction remain unmeasured; publication does not change those earlier verification limits.
