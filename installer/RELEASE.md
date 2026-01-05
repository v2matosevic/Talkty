# Talkty Release Guide

## Prerequisites

- .NET 8 SDK
- Inno Setup 6 (for installer): https://jrsoftware.org/isdl.php
- Windows SDK (for MSIX): https://developer.microsoft.com/windows/downloads/windows-sdk/
- Python 3 with Pillow (for asset generation): `pip install Pillow`

## Version Bumping

Before release, update version in these files:
1. `Talkty.App/Talkty.App.csproj` - Version, AssemblyVersion, FileVersion
2. `installer/setup.iss` - MyAppVersion
3. `Talkty.App/Package.appxmanifest` - Identity Version

## Building for Direct Download (Inno Setup)

### Quick Build
```powershell
cd installer
.\build.ps1
```

### Manual Steps
1. Publish the application:
   ```powershell
   cd Talkty.App
   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
   ```

2. Build installer with Inno Setup:
   - Open `installer/setup.iss` in Inno Setup Compiler
   - Press Ctrl+F9 to compile
   - Output: `dist/TalktySetup-{version}.exe`

## Building for Microsoft Store (MSIX)

### Quick Build
```powershell
cd installer
.\build-msix.ps1
```

### Manual Steps
1. Generate Store assets (if not done):
   ```powershell
   python installer/generate_store_assets.py
   ```

2. Publish for MSIX (non-single-file):
   ```powershell
   cd Talkty.App
   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
   ```

3. Create MSIX package:
   ```powershell
   makeappx pack /d "Talkty.App\bin\Release\net8.0-windows\win-x64\publish" /p "dist\Talkty.msix"
   ```

## Microsoft Store Submission

### Partner Center Setup
1. Create account at https://partner.microsoft.com/dashboard
2. Register as an app developer ($19 one-time fee)
3. Reserve app name "Talkty"

### Identity Configuration
After reserving the app name, update `Package.appxmanifest`:
```xml
<Identity
    Name="[Assigned Package Name]"
    Publisher="CN=[Your Publisher ID]"
    Version="1.0.0.0" />
```

### Required Store Assets
Upload to Partner Center:
- Screenshots (1366x768 minimum)
- App icon (300x300)
- Store listing description
- Privacy policy URL

### Certification Requirements
- App must declare microphone capability
- Privacy policy must be accessible online
- No code signing certificate needed (Microsoft signs)

## File Checklist

### Legal Documents
- [ ] LICENSE (proprietary EULA)
- [ ] THIRD_PARTY_LICENSES
- [ ] PRIVACY_POLICY.txt

### Store Assets (in Talkty.App/Assets/)
- [ ] Square44x44Logo.png + scale variants
- [ ] Square71x71Logo.png + scale variants
- [ ] Square150x150Logo.png + scale variants
- [ ] Square310x310Logo.png + scale variants
- [ ] Wide310x150Logo.png + scale variants
- [ ] StoreLogo.png + scale variants
- [ ] SplashScreen.png + scale variants

### Build Outputs
- [ ] `dist/TalktySetup-{version}.exe` (Inno Setup installer)
- [ ] `dist/Talkty-{version}.msix` (Microsoft Store package)

## Testing Checklist

Before release:
- [ ] Fresh install on clean Windows 11
- [ ] Verify Alt+Q hotkey works
- [ ] Test speech transcription
- [ ] Verify clipboard paste
- [ ] Test auto-paste (optional feature)
- [ ] Check system tray icon
- [ ] Verify About dialog version
- [ ] Test uninstallation
- [ ] Test startup option (run at login)

## Notes

- Published EXE is ~350MB (self-contained with .NET runtime + Whisper)
- Installer creates ~100MB compressed package
- Models must be downloaded separately by user
- App stores data in `%APPDATA%\Talkty\`
