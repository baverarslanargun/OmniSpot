; OmniSpot V1.0 Installer Script
; Inno Setup 6.x

#define MyAppName "OmniSpot"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "OmniSpot"
#define MyAppURL "https://github.com/yourusername/omnispot"
#define MyAppExeName "OmniSpot.exe"

[Setup]
; NOTE: The value of AppId uniquely identifies this application.
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
; Output settings
OutputDir=output
OutputBaseFilename=OmniSpot-1.0.0-Setup
; Compression
Compression=lzma2/ultra64
SolidCompression=yes
; Appearance
SetupIconFile=..\SmartFileLauncher.UI\Resources\app.ico
WizardStyle=modern
; Privileges - per-user install (no admin required)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; Uninstaller
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Windows başlangıcında çalıştır"; GroupDescription: "Ek seçenekler:"; Flags: unchecked

[Files]
Source: "..\publish\OmniSpot.exe"; DestDir: "{app}"; DestName: "{#MyAppExeName}"; Flags: ignoreversion
; Icon file
Source: "..\SmartFileLauncher.UI\Resources\app.ico"; DestDir: "{app}"; DestName: "omnispot.ico"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\omnispot.ico"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\omnispot.ico"; Tasks: desktopicon

[Registry]
; Startup entry (if user selects it)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// Custom code for additional functionality

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Post-install actions can be added here
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  // Pre-install checks can be added here
end;
