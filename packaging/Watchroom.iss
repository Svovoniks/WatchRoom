#ifndef AppVersion
#define AppVersion "0.2.0"
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
Filename: "{app}\Watchroom.exe"; Flags: nowait; Check: IsWatchroomUpdate

[Code]
function IsWatchroomUpdate: Boolean;
begin
  Result := ExpandConstant('{param:WATCHROOMUPDATE|0}') = '1';
end;
