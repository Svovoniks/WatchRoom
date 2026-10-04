#ifndef AppVersion
#error "Build with scripts/package.ps1 so the installer uses the app version"
#endif
[Setup]
AppId={{90B3BA58-BBC8-4993-A0EA-25C68D8B1DA6}
AppName=Watchroom
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Watchroom
DefaultGroupName=Watchroom
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=Watchroom-Setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Watchroom.exe
CloseApplications=yes
[Files]
Source: "..\artifacts\Watchroom\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Watchroom"; Filename: "{app}\Watchroom.exe"
[Run]
Filename: "{app}\Watchroom.exe"; Description: "Open Watchroom"; Flags: nowait postinstall skipifsilent

[Code]
function IsWatchroomUpdate: Boolean;
begin
  Result := ExpandConstant('{param:WATCHROOMUPDATE|0}') = '1';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode, Attempts: Integer;
  ReadyFile: String;
  RelaunchPage: TOutputMarqueeProgressWizardPage;
begin
  if (CurStep = ssPostInstall) and IsWatchroomUpdate then
  begin
    ReadyFile := ExpandConstant('{localappdata}\Watchroom\updates\ready');
    DeleteFile(ReadyFile);
    RelaunchPage := CreateOutputMarqueeProgressPage('Opening Watchroom', 'Your update is installed. Watchroom is starting...');
    RelaunchPage.SetText('Opening Watchroom...', 'Your library and settings are kept.');
    RelaunchPage.Show;
    try
      if Exec(ExpandConstant('{app}\Watchroom.exe'), '--updated', ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, ResultCode) then
      begin
        Attempts := 0;
        while (not FileExists(ReadyFile)) and (Attempts < 600) do
        begin
          Sleep(100);
          RelaunchPage.Animate;
          Attempts := Attempts + 1;
        end;
        DeleteFile(ReadyFile);
      end;
    finally
      RelaunchPage.Hide;
    end;
  end;
end;
