; Talkty Installer Script for Inno Setup 6
; Version2 - https://version2.hr

#define MyAppName "Talkty"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Version2"
#define MyAppURL "https://version2.hr"
#define MyAppExeName "Talkty.App.exe"
#define MyAppDescription "Local speech-to-text powered by Whisper"

[Setup]
; Unique ID for this application (generate new GUID for each app)
AppId={{B8E2A7F1-4C3D-5E6F-9A8B-0C1D2E3F4A5B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=TalktySetup-{#MyAppVersion}
SetupIconFile=..\Talkty.App\Resources\tray.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=100
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Installer appearance
WizardImageFile=compiler:WizModernImage.bmp
WizardSmallImageFile=compiler:WizModernSmallImage.bmp

; Version info embedded in installer
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppDescription}
VersionInfoCopyright=Copyright (c) 2025 {#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupentry"; Description: "Start {#MyAppName} when Windows starts"; GroupDescription: "System Integration:"; Flags: unchecked

[Files]
; Main application (published as single file)
Source: "..\Talkty.App\bin\Release\net8.0-windows\win-x64\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

; CUDA runtime files (if present, for GPU acceleration)
Source: "..\Talkty.App\bin\Release\net8.0-windows\win-x64\publish\runtimes\*"; DestDir: "{app}\runtimes"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

; License and documentation
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_LICENSES"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\PRIVACY_POLICY.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; Whisper models directory (user should place models here)
; Note: Models are downloaded separately due to size (~100MB-1GB per model)

[Dirs]
Name: "{app}\models"; Flags: uninsneveruninstall

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Run at startup (optional task)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startupentry

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// Check for .NET 8 Desktop Runtime
function IsDotNet8Installed(): Boolean;
var
  ResultCode: Integer;
begin
  // Try to run dotnet and check version
  Result := Exec('dotnet', '--list-runtimes', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if Result then
    Result := (ResultCode = 0);
end;

procedure InitializeWizard();
begin
  // Add custom initialization here if needed
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  // On ready page, check for .NET runtime
  if CurPageID = wpReady then
  begin
    // Note: Self-contained app doesn't require .NET runtime check
    // This is kept for reference if using framework-dependent deployment
  end;
end;

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nTalkty is a local speech-to-text application powered by Whisper AI. All processing happens on your device - no internet required for transcription.%n%nIt is recommended that you close all other applications before continuing.
