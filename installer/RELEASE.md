# Windows release guide

The supported installer is Inno Setup: `TalktySetup.iss`. The source is MIT-licensed.
MSIX files in this directory are experimental and are not the verified release path.

## Prepare source

1. Set the intended version in root `version.txt`. `Directory.Build.props` and the
   Inno script read it; do not duplicate assembly versions in the app project.
2. Document all changes since the previous public tag, optional external services,
   privacy/cost implications, and measured limits in `docs/RELEASE-<version>.md`.
3. Commit the source that will be compiled. Run the complete Release test suite and
   obtain passing GitHub CI for that source. Keep credentials, models, personal
   recordings, settings and generated build files out of Git.
4. Keep public `version.json` on the current public version until the new release
   and its downloadable installer are published. A draft is not a release notice.

## Build

Requirements: Windows x64, .NET 8 SDK, Inno Setup 6.
Run from the repository root:

```powershell
pwsh -File installer/build.ps1
```

The script restores, builds and tests, publishes self-contained into the fresh
`installer/output/release-<version>` directory, validates native runtimes, and
compiles `installer/output/TalktySetup-<version>.exe`. It also creates a payload
CSV with each file's SHA-256 and an installer `.sha256` file.

Whisper requires the runtimes directory. Do not use single-file publication.
The standard installer includes Vulkan and native Opus; CUDA stays a separate
optional pack. Existing publish directories are preserved, not reused or deleted.
For another build use a new `-PublishDirectory`.

`-SkipChecks` is for a recorded passing result with unchanged relevant source,
dependencies and environment. `-SkipPublish -PublishDirectory <verified-layout>`
recompiles an installer from that layout after checking its version/payload.
`-SkipInstaller` publishes and manifests without compiling Inno Setup.

## Verify and install

- Record source commit, product version, installer size/hash and payload count.
- Inspect the actual Settings/overlay layouts affected by the release. Use the
  existing off-screen WPF tests; do not inject input into somebody else's desktop.
- Save local hashes of settings, history, recovery files, models and installed CUDA
  files before the update. Keep those manifests and backups out of Git.
- End an idle running instance, then run the official installer over its current
  location. Avoid mirror-copy/delete operations that remove the uninstaller or CUDA.
- Existing all-users installations require an elevated installer/UAC approval even
  though fresh per-user installations normally do not. A silent exit code 2 is not
  success. Record the exit code and install log.
- Compare every installed payload file against the manifest, verify Windows version
  registration, and compare the preserved data/CUDA hashes before relaunch.
- Launch as the normal user. Verify executable path, startup version, model/backend,
  hotkey registration and absence of startup errors. Real microphone/desktop behavior
  is a separate user check; headless tests do not prove those interactions.
- State explicitly whether clean-machine install/uninstall checks were performed.

## Prepare GitHub

Create a draft release with the exact source commit as its target, upload the
installer and checksum, and verify their downloaded hashes. Include the release
notes and known limits. A public tag, Latest marker and `version.json` update belong
to publication, after that action is authorized. Draft preparation and local
installation do not imply public publication or clean-machine validation.

The optional `cuda-pack-cu13` asset is version-independent; do not replace it for
an ordinary application release. Keep the dated delivery record and actual
installation/publication status current.
