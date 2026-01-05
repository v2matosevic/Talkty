# Talkty

**Local speech-to-text for Windows, powered by Whisper.**

Talkty transcribes your voice to text entirely on your device. No internet required. No data leaves your computer.

---

## Features

- **100% Local** - All processing happens on your device using Whisper
- **Fast** - Optimized Whisper.cpp engine with optional GPU acceleration
- **Simple** - Press hotkey, speak, get text in clipboard
- **Auto-paste** - Optionally paste transcription at cursor position
- **Customizable** - Choose model size and configure hotkey
- **Multi-monitor** - Recording overlay appears on active screen
- **Background operation** - Runs quietly in system tray

---

## Quick Start

1. **Download** `TalktySetup-1.0.1.exe` from Releases
2. **Install** and launch Talkty
3. **Download a model** in Settings (Base recommended)
4. **Press Alt+Q** to record, speak, press again to stop
5. **Paste** (Ctrl+V) - transcription is on your clipboard

---

## Model Options

| Model | Size | Speed | Quality | Best For |
|-------|------|-------|---------|----------|
| Tiny | 75 MB | Fastest | Good | Quick notes, simple phrases |
| Base | 142 MB | Fast | Better | **General use (recommended)** |
| Small | 488 MB | Medium | Great | Longer dictation, accuracy |
| Large | 3 GB | Slow | Best | Maximum accuracy needed |

Models are downloaded automatically from HuggingFace when selected in Settings.

---

## System Requirements

- Windows 10/11 (64-bit)
- 4 GB RAM minimum (8 GB for Large model)
- ~150 MB disk space + model size
- Microphone

**Optional:** NVIDIA GPU with CUDA for faster transcription

---

## Settings

Access via gear icon or right-click tray icon → Settings.

| Setting | Description |
|---------|-------------|
| Model | Whisper model (Tiny/Base/Small/Large) |
| Microphone | Audio input device |
| Hotkey | Global shortcut (default: Alt+Q) |
| Copy to Clipboard | Auto-copy transcription (default: on) |
| Auto-paste | Paste at cursor after transcription |

---

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| Alt+Q | Start/stop recording (configurable) |
| Double-click tray | Open main window |

---

## Privacy

All processing is local:
- Audio never leaves your device
- No cloud services or internet required
- Audio discarded after transcription
- No telemetry or tracking

---

## Troubleshooting

### "Failed to load model"
- Ensure model download completed in Settings
- Check `%AppData%\Talkty\Models\` for the .bin file
- Try re-downloading the model

### Hotkey not working
- Another app may use the same shortcut
- Change hotkey in Settings
- Restart Talkty after changing

### Transcription inaccurate
- Use a larger model (Small or Large)
- Speak clearly, reduce background noise
- Move closer to microphone

### Auto-paste not working
- Some apps block simulated input
- Works best with standard text fields
- Fallback: manual Ctrl+V paste

---

## Building from Source

### Prerequisites
- .NET 8.0 SDK
- Windows 10/11
- Inno Setup 6 (for installer)

### Development
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

### Create Installer
```bash
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\TalktySetup.iss
```

---

## Project Structure

```
Talkty/
├── Talkty.App/
│   ├── Controls/        # UI components (ToastNotification)
│   ├── Converters/      # XAML value converters
│   ├── Models/          # Data models (AppSettings, ModelProfile)
│   ├── Resources/       # Styles, icons
│   ├── Services/        # Core logic (Audio, Transcription, Hotkey)
│   ├── ViewModels/      # MVVM ViewModels
│   └── Views/           # XAML windows
├── installer/           # Inno Setup scripts
│   └── TalktySetup.iss
├── CLAUDE.md            # Developer documentation
└── README.md            # This file
```

---

## Tech Stack

- **.NET 8.0 / WPF** - UI framework
- **[Whisper.net](https://github.com/sandrohanea/whisper.net)** - Whisper.cpp .NET bindings
- **[NAudio](https://github.com/naudio/NAudio)** - Audio capture
- **[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)** - MVVM framework
- **[Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon)** - System tray

---

## Version History

### v1.0.1 (December 2025)
- Fixed microphone test button
- Added crash logging for troubleshooting
- Added update notification system
- Upgrade: just run new installer (no uninstall needed)

### v1.0.0 (December 2025)
- Initial release
- Local Whisper transcription
- Customizable global hotkey
- Multi-monitor support
- Auto-paste feature
- In-app model downloads
- Professional installer

---

## License

Copyright (c) 2025 Version2. All rights reserved.

---

## Support

- Website: [version2.hr](https://version2.hr)
