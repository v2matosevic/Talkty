# Talkty - Claude Code Documentation

This document provides context for Claude Code to understand and work with the Talkty codebase effectively.

---

## Project Overview

**Talkty** is a local speech-to-text Windows desktop application powered by Whisper. It runs entirely offline, ensuring privacy. Users press a global hotkey to record speech, which is transcribed and automatically copied to the clipboard.

**Version:** 1.0.4
**Status:** Production-ready
**Platform:** Windows 10/11 (x64)

---

## Architecture

```
Talkty.App/
├── App.xaml(.cs)              # Application entry, single-instance check, global resources
├── MainWindow.xaml(.cs)       # Main UI window, tray icon, event wiring
├── Controls/
│   └── ToastNotification      # In-app toast notification component
├── Converters/                # XAML value converters
├── Models/
│   ├── AppSettings.cs         # User settings + UserHints for UX tracking
│   └── ModelProfile.cs        # Whisper model definitions (Tiny, Base, Small, Large)
├── Resources/
│   ├── Styles.xaml            # Dark theme, colors, button styles, effects
│   └── tray.ico               # System tray icon
├── Services/
│   ├── AudioCaptureService    # NAudio-based microphone recording (16kHz mono)
│   ├── ClipboardService       # Windows clipboard operations
│   ├── HotkeyService          # Global hotkey registration (Win32 API)
│   ├── LoggingService         # File logging + crash reports to app directory
│   ├── ModelDownloadService   # Downloads Whisper models from HuggingFace
│   ├── SettingsService        # JSON settings persistence
│   ├── TranscriptionService   # Whisper.net integration
│   └── UpdateService          # Checks for app updates from remote JSON
├── ViewModels/
│   ├── MainViewModel          # Main window logic, recording state machine
│   ├── OverlayViewModel       # Floating recording indicator state
│   └── SettingsViewModel      # Settings dialog logic
└── Views/
    ├── AboutWindow            # About dialog
    ├── OnboardingWindow       # First-run tutorial
    ├── OverlayWindow          # Floating pill showing "Recording..." status
    └── SettingsWindow         # Model selection, hotkey config, preferences
```

---

## Tech Stack

| Component | Technology |
|-----------|------------|
| Framework | .NET 8.0, WPF |
| UI Pattern | MVVM (CommunityToolkit.Mvvm) |
| Audio Capture | NAudio 2.2.1 |
| Speech-to-Text | Whisper.net 1.7.4 (whisper.cpp binding) |
| System Tray | Hardcodet.NotifyIcon.Wpf |
| Serialization | System.Text.Json |

---

## Key Behaviors

### Recording Flow
1. User presses global hotkey (default: Alt+Q)
2. `MainViewModel.ToggleListening()` starts recording
3. `OverlayWindow` appears showing "Recording..." with timer
4. User presses hotkey again to stop
5. Audio converted to float samples → sent to Whisper
6. Transcription copied to clipboard
7. If AutoPaste enabled: focus restored to original window, Ctrl+V simulated

### Model Management
- Models stored in: `%AppData%/Talkty/Models/`
- Downloaded from: `https://huggingface.co/ggerganov/whisper.cpp/resolve/main/`
- Profiles: Tiny (75MB), Base (142MB), Small (488MB), Large (3GB)
- File size validation on download (SHA256 removed - hashes change on HuggingFace)

### Multi-Monitor Support
- Overlay positions on monitor where cursor is located
- Uses Win32 `MonitorFromPoint` + `GetMonitorInfo`
- Repositions on every show (not just first load)

### Auto-Paste Reliability
- Captures target window handle before recording starts
- Waits for modifier keys to release (user just pressed Alt+Q)
- Verifies clipboard has content before pasting
- Uses `AttachThreadInput` + `SetForegroundWindow` for focus
- Sends `Ctrl+V` via `SendInput`

---

## User Settings

Stored in: `%AppData%/Talkty/settings.json`

```json
{
  "modelProfile": "Base",
  "selectedMicrophoneId": null,
  "copyToClipboard": true,
  "autoPaste": false,
  "hotkeyModifier": "Alt",
  "hotkeyKey": "Q",
  "hints": {
    "hasSeenTrayMinimizeHint": false,
    "appLaunchCount": 1
  }
}
```

---

## UX Features (v1.0)

1. **Toast Notifications** - In-app feedback for clipboard copy, tips
2. **Tray Minimize Hint** - Balloon tip on first minimize explaining tray behavior
3. **Second Launch Tip** - Toast showing "Press hotkey from any app"
4. **Dynamic Hotkey Badge** - Shows current hotkey, updates when changed
5. **Tooltips** - Contextual help on key UI elements
6. **Styled Tooltips** - Dark theme matching app design

---

## Build & Publish

### Development Build
```bash
cd Talkty.App
dotnet build
dotnet run
```

### Release Build
```bash
cd Talkty.App
dotnet publish -c Release -r win-x64 --self-contained true
```

Output: `bin/Release/net8.0-windows/win-x64/publish/`

### Create Installer
```bash
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\TalktySetup.iss
```

Output: `installer/output/TalktySetup-1.0.1.exe`

---

## Important Implementation Notes

### Why Not Single-File Publish?
Whisper.net native DLLs (`whisper.dll`, `ggml-*.dll`) must be in the `runtimes/` folder structure. Single-file publish embeds .NET but doesn't properly extract native libraries at runtime.

### Native Library Resolution
Whisper.net looks for DLLs in:
- `runtimes/win-x64/` (CPU)
- `runtimes/cuda/win-x64/` (GPU with CUDA)

### Global Hotkey Registration
Uses Win32 `RegisterHotKey` API. Requires window handle. Re-registers when settings change.

### Clipboard Access
Must run on UI thread. Uses `Application.Current.Dispatcher.Invoke()`.

### Focus Restoration for Auto-Paste
Windows restricts `SetForegroundWindow`. Solution:
1. `AllowSetForegroundWindow(ASFW_ANY)`
2. `AttachThreadInput` between current and target thread
3. `BringWindowToTop` + `SetForegroundWindow`

---

## File Locations

| Item | Path |
|------|------|
| Settings | `%AppData%/Talkty/settings.json` |
| History | `%AppData%/Talkty/history.json` |
| Models | `%AppData%/Talkty/Models/` |
| Logs | `%AppData%/Talkty/Logs/talkty_YYYY-MM-DD_HH-mm-ss.log` |
| Crash Reports | `{app install dir}/crash_YYYY-MM-DD_HH-mm-ss.log` |

---

## Common Issues & Solutions

### "Native Library not found"
**Cause:** Whisper DLLs not deployed alongside exe
**Fix:** Don't use `PublishSingleFile=true`. Ensure `runtimes/` folder is included.

### Model download stuck at 100%
**Cause:** SHA256 verification failing (hashes outdated)
**Fix:** Removed SHA256 verification. File size check is sufficient.

### Auto-paste not working
**Cause:** Target window losing focus, modifier keys still held
**Fix:** Wait for key release, verify clipboard, retry focus restoration.

### Overlay on wrong monitor
**Cause:** Only positioning on first load
**Fix:** Reposition on every `IsVisibleChanged` event.

### Hotkey not registering
**Cause:** Another app using same hotkey
**Fix:** Show error message, let user choose different hotkey in settings.

---

## Version History

### v1.0.5 (2026-01-05)
- Added ESC key to cancel recording (registers only during active recording)
- Fixed volume not restoring after longer recordings (~20+ seconds) - COM object staleness issue
- Added configurable volume duck level with simple +/- controls (5% increments, range 5%-100%)
- Volume duck level setting appears below the checkbox when enabled
- Improved COM object handling: fresh references per operation prevent stale RCW exceptions
- Added retry logic for volume restore: if fade fails, attempts instant restore

### v1.0.4 (2026-01-05)
- Added volume ducking feature: automatically lowers system volume during recording
- Smooth 250ms fade in/out transitions for volume changes
- New "Lower volume while recording" option in Settings > Behavior section
- Volume automatically restored if app crashes or exits during recording

### v1.0.3 (2025-12-18)
- Fixed duplicate service instances in SettingsWindow (microphone test now uses correct device)
- Added model loading indicator with status feedback
- Added toast notifications on model load success/failure (shows model name and CPU/GPU)
- Improved language dropdown to show full language names instead of codes
- Improved GPU detection by using stored settings instead of brittle string parsing
- Added proper event unsubscription and disposal patterns
- Recording blocked during model load with user feedback

### v1.0.2 (2025-12-18)
- Added language selection dropdown in settings for explicit language choice
- Fixed overlay disappearing while recording still active (race condition)
- Fixed transcription in non-English languages (German, Croatian, etc.) by respecting user language setting
- Removed Parakeet/NVIDIA model support (was causing crashes)
- Cleaned up compiler warnings for production build

### v1.0.1 (2025-12-07)
- Fixed microphone test "Stop" button not responding
- Added crash logging (saves `crash_*.log` to app install directory)
- Added update notification system (checks `version.json` from website)
- Users can upgrade by running new installer over existing installation

### v1.0.0 (2025-12-07)
- Initial release
- Whisper-powered local transcription
- Global hotkey support with customization
- Multi-monitor overlay positioning
- Auto-paste to cursor
- Model download with progress
- Toast notifications and UX hints
- Professional installer with Inno Setup

---

## Future Considerations

- Code signing certificate for "Unknown Publisher" fix
- Smaller installer (remove unused runtimes like ARM64, x86)
- Portable version (no installer)
- Voice activity detection (auto-stop)
