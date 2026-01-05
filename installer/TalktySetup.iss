; Talkty Installer Script
; Built with Inno Setup 6

#define MyAppName "Talkty"
#define MyAppVersion "1.0.5"
#define MyAppPublisher "Version2"
#define MyAppURL "https://github.com/v2matosevic/Talkty"
#define MyAppExeName "Talkty.App.exe"
#define MyAppDescription "Local speech-to-text powered by Whisper"

[Setup]
; Application info
AppId={{A8B9C0D1-E2F3-4567-8901-234567890ABC}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; Installation paths
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; Output settings
OutputDir=..\installer\output
OutputBaseFilename=TalktySetup-{#MyAppVersion}
SetupIconFile=..\Talkty.App\Resources\tray.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

; Compression
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
LZMADictionarySize=65536
LZMANumFastBytes=273

; UI Settings
WizardStyle=modern
WizardSizePercent=100
DisableWelcomePage=no
LicenseFile=LICENSE.txt

; Privileges
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; Other settings
AllowNoIcons=yes
ShowLanguageDialog=auto
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Version info
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
Name: "startupicon"; Description: "Start Talkty when Windows starts"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
; All application files
Source: "..\Talkty.App\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*linux*,*.so"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Add to Windows startup if selected
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Talkty"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean up app data on uninstall (optional - ask user)
Type: filesandordirs; Name: "{userappdata}\Talkty"

[Code]
var
  DataDirPage: TInputOptionWizardPage;

procedure InitializeWizard;
begin
  // Add a custom page asking about keeping data on uninstall
end;

function InitializeUninstall(): Boolean;
var
  MsgResult: Integer;
begin
  Result := True;
  MsgResult := MsgBox('Do you want to remove your Talkty settings and downloaded models?' + #13#10 + #13#10 +
                      'Click Yes to remove all data, or No to keep your settings for future reinstallation.',
                      mbConfirmation, MB_YESNO);
  if MsgResult = IDNO then
  begin
    // User wants to keep data - remove the uninstall delete entry
    UnloadDLL(ExpandConstant('{app}\Talkty.App.exe'));
  end;
end;
