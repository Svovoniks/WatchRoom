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
ChangesAssociations=yes
[Files]
Source: "..\artifacts\Watchroom\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Watchroom"; Filename: "{app}\Watchroom.exe"
[Registry]
; Offers Watchroom in Open with and Default apps for the video types it scans; Windows leaves the default to the user.
Root: HKCU; Subkey: "Software\Classes\Watchroom.Video"; ValueType: string; ValueName: ""; ValueData: "Video"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Watchroom.Video"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "Video"
Root: HKCU; Subkey: "Software\Classes\Watchroom.Video\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Watchroom.exe,0"
Root: HKCU; Subkey: "Software\Classes\Watchroom.Video\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Watchroom.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\Applications\Watchroom.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "Watchroom"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\Watchroom.exe\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Watchroom.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Watchroom\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "Watchroom"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Watchroom\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Watch your videos alone or together"
Root: HKCU; Subkey: "Software\Watchroom\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{app}\Watchroom.exe,0"
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "Watchroom"; ValueData: "Software\Watchroom\Capabilities"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mkv\OpenWithProgids"; ValueType: string; ValueName: "Watchroom.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\Watchroom.exe\SupportedTypes"; ValueType: string; ValueName: ".mkv"; ValueData: ""
Root: HKCU; Subkey: "Software\Watchroom\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkv"; ValueData: "Watchroom.Video"
Root: HKCU; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: string; ValueName: "Watchroom.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\Watchroom.exe\SupportedTypes"; ValueType: string; ValueName: ".mp4"; ValueData: ""
Root: HKCU; Subkey: "Software\Watchroom\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mp4"; ValueData: "Watchroom.Video"
Root: HKCU; Subkey: "Software\Classes\.m4v\OpenWithProgids"; ValueType: string; ValueName: "Watchroom.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\Watchroom.exe\SupportedTypes"; ValueType: string; ValueName: ".m4v"; ValueData: ""
Root: HKCU; Subkey: "Software\Watchroom\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m4v"; ValueData: "Watchroom.Video"
Root: HKCU; Subkey: "Software\Classes\.avi\OpenWithProgids"; ValueType: string; ValueName: "Watchroom.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\Watchroom.exe\SupportedTypes"; ValueType: string; ValueName: ".avi"; ValueData: ""
Root: HKCU; Subkey: "Software\Watchroom\Capabilities\FileAssociations"; ValueType: string; ValueName: ".avi"; ValueData: "Watchroom.Video"
Root: HKCU; Subkey: "Software\Classes\.mov\OpenWithProgids"; ValueType: string; ValueName: "Watchroom.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\Watchroom.exe\SupportedTypes"; ValueType: string; ValueName: ".mov"; ValueData: ""
Root: HKCU; Subkey: "Software\Watchroom\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mov"; ValueData: "Watchroom.Video"
Root: HKCU; Subkey: "Software\Classes\.webm\OpenWithProgids"; ValueType: string; ValueName: "Watchroom.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\Watchroom.exe\SupportedTypes"; ValueType: string; ValueName: ".webm"; ValueData: ""
Root: HKCU; Subkey: "Software\Watchroom\Capabilities\FileAssociations"; ValueType: string; ValueName: ".webm"; ValueData: "Watchroom.Video"
Root: HKCU; Subkey: "Software\Classes\.mpeg\OpenWithProgids"; ValueType: string; ValueName: "Watchroom.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\Watchroom.exe\SupportedTypes"; ValueType: string; ValueName: ".mpeg"; ValueData: ""
Root: HKCU; Subkey: "Software\Watchroom\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mpeg"; ValueData: "Watchroom.Video"
Root: HKCU; Subkey: "Software\Classes\.mpg\OpenWithProgids"; ValueType: string; ValueName: "Watchroom.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\Watchroom.exe\SupportedTypes"; ValueType: string; ValueName: ".mpg"; ValueData: ""
Root: HKCU; Subkey: "Software\Watchroom\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mpg"; ValueData: "Watchroom.Video"
Root: HKCU; Subkey: "Software\Classes\.ts\OpenWithProgids"; ValueType: string; ValueName: "Watchroom.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\Watchroom.exe\SupportedTypes"; ValueType: string; ValueName: ".ts"; ValueData: ""
Root: HKCU; Subkey: "Software\Watchroom\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ts"; ValueData: "Watchroom.Video"
Root: HKCU; Subkey: "Software\Classes\.m2ts\OpenWithProgids"; ValueType: string; ValueName: "Watchroom.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\Watchroom.exe\SupportedTypes"; ValueType: string; ValueName: ".m2ts"; ValueData: ""
Root: HKCU; Subkey: "Software\Watchroom\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m2ts"; ValueData: "Watchroom.Video"
Root: HKCU; Subkey: "Software\Classes\.wmv\OpenWithProgids"; ValueType: string; ValueName: "Watchroom.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\Watchroom.exe\SupportedTypes"; ValueType: string; ValueName: ".wmv"; ValueData: ""
Root: HKCU; Subkey: "Software\Watchroom\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wmv"; ValueData: "Watchroom.Video"
Root: HKCU; Subkey: "Software\Classes\.flv\OpenWithProgids"; ValueType: string; ValueName: "Watchroom.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\Watchroom.exe\SupportedTypes"; ValueType: string; ValueName: ".flv"; ValueData: ""
Root: HKCU; Subkey: "Software\Watchroom\Capabilities\FileAssociations"; ValueType: string; ValueName: ".flv"; ValueData: "Watchroom.Video"
Root: HKCU; Subkey: "Software\Classes\.ogv\OpenWithProgids"; ValueType: string; ValueName: "Watchroom.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\Watchroom.exe\SupportedTypes"; ValueType: string; ValueName: ".ogv"; ValueData: ""
Root: HKCU; Subkey: "Software\Watchroom\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ogv"; ValueData: "Watchroom.Video"
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
