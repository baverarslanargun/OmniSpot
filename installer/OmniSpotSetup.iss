#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef UiPublishDir
  #define UiPublishDir "..\artifacts\demo-installer\ui"
#endif
#ifndef ServicePublishDir
  #define ServicePublishDir "..\artifacts\demo-installer\service"
#endif

#define MyAppName "OmniSpot Demo"
#define MyAppPublisher "OmniSpot"
#define MyAppURL "https://github.com/baverarslanargun/OmniSpot"
#define MyAppExeName "OmniSpot.exe"
#define ServiceExeName "OmniSpot.ChangeFeedService.exe"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf64}\OmniSpot
DisableDirPage=yes
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=output
OutputBaseFilename=OmniSpot-{#MyAppVersion}-Demo-Setup
Compression=lzma2/ultra64
SolidCompression=yes
SetupIconFile=..\SmartFileLauncher.UI\Resources\app.ico
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
RestartIfNeededByRun=no
Uninstallable=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=OmniSpot geçici demo kurulumu

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#UiPublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion restartreplace
Source: "{#ServicePublishDir}\{#ServiceExeName}"; DestDir: "{app}\service"; Flags: ignoreversion restartreplace
Source: "Manage-ChangeFeedService.ps1"; DestDir: "{app}\service"; Flags: ignoreversion
Source: "..\SmartFileLauncher.UI\Resources\app.ico"; DestDir: "{app}"; DestName: "omnispot.ico"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\omnispot.ico"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\omnispot.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,OmniSpot}"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Code]
const
  ServiceName = 'OmniSpotChangeFeed';
  ServiceRegistryKey = 'SYSTEM\CurrentControlSet\Services\OmniSpotChangeFeed';

function ServiceExists(): Boolean;
begin
  Result := RegKeyExists(HKLM, ServiceRegistryKey);
end;

function RunServiceManager(Action: String): Boolean;
var
  PowerShellPath: String;
  ManagerPath: String;
  ServiceExePath: String;
  Parameters: String;
  ResultCode: Integer;
begin
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  ManagerPath := ExpandConstant('{app}\service\Manage-ChangeFeedService.ps1');
  ServiceExePath := ExpandConstant('{app}\service\{#ServiceExeName}');
  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
    AddQuotes(ManagerPath) + ' -Action ' + Action + ' -ServiceExePath ' +
    AddQuotes(ServiceExePath);

  Result := Exec(
    PowerShellPath,
    Parameters,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) and (ResultCode = 0);
end;

function StopApplicationForUninstall(): Boolean;
var
  ApplicationPath: String;
  ResultCode: Integer;
begin
  ApplicationPath := ExpandConstant('{app}\{#MyAppExeName}');
  if not FileExists(ApplicationPath) then
  begin
    Result := True;
    Exit;
  end;

  Result := Exec(
    ApplicationPath,
    '--shutdown-for-uninstall',
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) and (ResultCode = 0);
end;

function InitializeSetup(): Boolean;
begin
  Result := False;

  if not IsWin64 then
  begin
    MsgBox('OmniSpot yalnızca 64-bit Windows üzerinde kurulabilir.', mbCriticalError, MB_OK);
    Exit;
  end;

  if ServiceExists() then
  begin
    MsgBox(
      ServiceName + ' adlı servis zaten kurulu. Geçici installer mevcut servisi ' +
      'devralmaz. Önce servisin ait olduğu kurulumu kaldırın.',
      mbCriticalError,
      MB_OK);
    Exit;
  end;

  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not RunServiceManager('Install') then
      RaiseException(
        'OmniSpotChangeFeed servisi kurulamadı veya hazır duruma gelmedi. ' +
        'Servis kaydı geri alınmaya çalışıldı.');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    if not StopApplicationForUninstall() then
      RaiseException(
        'OmniSpot uygulaması güvenli biçimde kapatılamadı. ' +
        'Program dosyaları korunuyor.');

    if not RunServiceManager('Uninstall') then
      RaiseException(
        'OmniSpotChangeFeed servisi güvenli biçimde kaldırılamadı. ' +
        'Program dosyaları korunuyor.');
  end;
end;
