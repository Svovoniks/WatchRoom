#ifndef AppVersion
#error "Build with scripts/package.ps1 so the installer uses the app version"
#endif
; UpdateFrom builds an update installer from scripts/package-updates.ps1: only the files staged in AppFiles, plus
; deletion of files the new version no longer has, applied over an installed UpdateFrom. TestInstall gives the update
; checks their own identity, without file associations or shortcuts, so they never touch the real installation.
#ifndef AppGuid
#define AppGuid "90B3BA58-BBC8-4993-A0EA-25C68D8B1DA6"
#endif
#ifndef AppFiles
#define AppFiles "..\artifacts\BlackPearl"
#endif
#ifndef OutputFolder
#define OutputFolder "..\artifacts"
#endif
[Setup]
AppId={{{#AppGuid}}
#ifdef TestInstall
AppName=BlackPearl update check
#else
AppName=BlackPearl
#endif
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\BlackPearl
DefaultGroupName=BlackPearl
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputFolder}
#ifdef UpdateFrom
OutputBaseFilename=BlackPearl-Update-{#UpdateFrom}-to-{#AppVersion}-win-x64
#else
OutputBaseFilename=BlackPearl-Setup-{#AppVersion}-win-x64
#endif
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\BlackPearl.exe
CloseApplications=yes
#ifndef TestInstall
ChangesAssociations=yes
; Installations from before the rename keep their folder (and their registration's install location), but their Start
; menu folder moves from Watchroom to BlackPearl.
UsePreviousGroup=no
#endif
[InstallDelete]
; The app was called Watchroom before: its Watchroom.exe and Watchroom.*.dll are replaced by the BlackPearl ones.
Type: files; Name: "{app}\Watchroom.*"
#ifndef TestInstall
Type: files; Name: "{autoprograms}\Watchroom\Watchroom.lnk"
Type: dirifempty; Name: "{autoprograms}\Watchroom"
#endif
[Files]
#ifdef UpdateFrom
Source: "{#AppFiles}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
#include UpdateRemoved
#else
Source: "{#AppFiles}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif
#ifndef TestInstall
[Icons]
Name: "{group}\BlackPearl"; Filename: "{app}\BlackPearl.exe"
[Registry]
; Removes the Open with and Default apps registration from before the app was renamed from Watchroom, which named a
; Watchroom.exe that no longer exists.
Root: HKCU; Subkey: "Software\Classes\Watchroom.Video"; ValueType: none; Flags: deletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Applications\Watchroom.exe"; ValueType: none; Flags: deletekey dontcreatekey
Root: HKCU; Subkey: "Software\Watchroom"; ValueType: none; Flags: deletekey dontcreatekey
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: none; ValueName: "Watchroom"; Flags: deletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\.mkv\OpenWithProgids"; ValueType: none; ValueName: "Watchroom.Video"; Flags: deletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: none; ValueName: "Watchroom.Video"; Flags: deletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\.m4v\OpenWithProgids"; ValueType: none; ValueName: "Watchroom.Video"; Flags: deletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\.avi\OpenWithProgids"; ValueType: none; ValueName: "Watchroom.Video"; Flags: deletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\.mov\OpenWithProgids"; ValueType: none; ValueName: "Watchroom.Video"; Flags: deletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\.webm\OpenWithProgids"; ValueType: none; ValueName: "Watchroom.Video"; Flags: deletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\.mpeg\OpenWithProgids"; ValueType: none; ValueName: "Watchroom.Video"; Flags: deletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\.mpg\OpenWithProgids"; ValueType: none; ValueName: "Watchroom.Video"; Flags: deletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\.ts\OpenWithProgids"; ValueType: none; ValueName: "Watchroom.Video"; Flags: deletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\.m2ts\OpenWithProgids"; ValueType: none; ValueName: "Watchroom.Video"; Flags: deletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\.wmv\OpenWithProgids"; ValueType: none; ValueName: "Watchroom.Video"; Flags: deletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\.flv\OpenWithProgids"; ValueType: none; ValueName: "Watchroom.Video"; Flags: deletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\.ogv\OpenWithProgids"; ValueType: none; ValueName: "Watchroom.Video"; Flags: deletevalue dontcreatekey
; Offers BlackPearl in Open with and Default apps for the video types it scans; Windows leaves the default to the user.
Root: HKCU; Subkey: "Software\Classes\BlackPearl.Video"; ValueType: string; ValueName: ""; ValueData: "Video"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\BlackPearl.Video"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "Video"
Root: HKCU; Subkey: "Software\Classes\BlackPearl.Video\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\BlackPearl.exe,0"
Root: HKCU; Subkey: "Software\Classes\BlackPearl.Video\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\BlackPearl.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\Applications\BlackPearl.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "BlackPearl"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\BlackPearl.exe\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\BlackPearl.exe"" ""%1"""
Root: HKCU; Subkey: "Software\BlackPearl\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "BlackPearl"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\BlackPearl\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Watch your videos alone or together"
Root: HKCU; Subkey: "Software\BlackPearl\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{app}\BlackPearl.exe,0"
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "BlackPearl"; ValueData: "Software\BlackPearl\Capabilities"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mkv\OpenWithProgids"; ValueType: string; ValueName: "BlackPearl.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\BlackPearl.exe\SupportedTypes"; ValueType: string; ValueName: ".mkv"; ValueData: ""
Root: HKCU; Subkey: "Software\BlackPearl\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkv"; ValueData: "BlackPearl.Video"
Root: HKCU; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: string; ValueName: "BlackPearl.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\BlackPearl.exe\SupportedTypes"; ValueType: string; ValueName: ".mp4"; ValueData: ""
Root: HKCU; Subkey: "Software\BlackPearl\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mp4"; ValueData: "BlackPearl.Video"
Root: HKCU; Subkey: "Software\Classes\.m4v\OpenWithProgids"; ValueType: string; ValueName: "BlackPearl.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\BlackPearl.exe\SupportedTypes"; ValueType: string; ValueName: ".m4v"; ValueData: ""
Root: HKCU; Subkey: "Software\BlackPearl\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m4v"; ValueData: "BlackPearl.Video"
Root: HKCU; Subkey: "Software\Classes\.avi\OpenWithProgids"; ValueType: string; ValueName: "BlackPearl.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\BlackPearl.exe\SupportedTypes"; ValueType: string; ValueName: ".avi"; ValueData: ""
Root: HKCU; Subkey: "Software\BlackPearl\Capabilities\FileAssociations"; ValueType: string; ValueName: ".avi"; ValueData: "BlackPearl.Video"
Root: HKCU; Subkey: "Software\Classes\.mov\OpenWithProgids"; ValueType: string; ValueName: "BlackPearl.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\BlackPearl.exe\SupportedTypes"; ValueType: string; ValueName: ".mov"; ValueData: ""
Root: HKCU; Subkey: "Software\BlackPearl\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mov"; ValueData: "BlackPearl.Video"
Root: HKCU; Subkey: "Software\Classes\.webm\OpenWithProgids"; ValueType: string; ValueName: "BlackPearl.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\BlackPearl.exe\SupportedTypes"; ValueType: string; ValueName: ".webm"; ValueData: ""
Root: HKCU; Subkey: "Software\BlackPearl\Capabilities\FileAssociations"; ValueType: string; ValueName: ".webm"; ValueData: "BlackPearl.Video"
Root: HKCU; Subkey: "Software\Classes\.mpeg\OpenWithProgids"; ValueType: string; ValueName: "BlackPearl.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\BlackPearl.exe\SupportedTypes"; ValueType: string; ValueName: ".mpeg"; ValueData: ""
Root: HKCU; Subkey: "Software\BlackPearl\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mpeg"; ValueData: "BlackPearl.Video"
Root: HKCU; Subkey: "Software\Classes\.mpg\OpenWithProgids"; ValueType: string; ValueName: "BlackPearl.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\BlackPearl.exe\SupportedTypes"; ValueType: string; ValueName: ".mpg"; ValueData: ""
Root: HKCU; Subkey: "Software\BlackPearl\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mpg"; ValueData: "BlackPearl.Video"
Root: HKCU; Subkey: "Software\Classes\.ts\OpenWithProgids"; ValueType: string; ValueName: "BlackPearl.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\BlackPearl.exe\SupportedTypes"; ValueType: string; ValueName: ".ts"; ValueData: ""
Root: HKCU; Subkey: "Software\BlackPearl\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ts"; ValueData: "BlackPearl.Video"
Root: HKCU; Subkey: "Software\Classes\.m2ts\OpenWithProgids"; ValueType: string; ValueName: "BlackPearl.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\BlackPearl.exe\SupportedTypes"; ValueType: string; ValueName: ".m2ts"; ValueData: ""
Root: HKCU; Subkey: "Software\BlackPearl\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m2ts"; ValueData: "BlackPearl.Video"
Root: HKCU; Subkey: "Software\Classes\.wmv\OpenWithProgids"; ValueType: string; ValueName: "BlackPearl.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\BlackPearl.exe\SupportedTypes"; ValueType: string; ValueName: ".wmv"; ValueData: ""
Root: HKCU; Subkey: "Software\BlackPearl\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wmv"; ValueData: "BlackPearl.Video"
Root: HKCU; Subkey: "Software\Classes\.flv\OpenWithProgids"; ValueType: string; ValueName: "BlackPearl.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\BlackPearl.exe\SupportedTypes"; ValueType: string; ValueName: ".flv"; ValueData: ""
Root: HKCU; Subkey: "Software\BlackPearl\Capabilities\FileAssociations"; ValueType: string; ValueName: ".flv"; ValueData: "BlackPearl.Video"
Root: HKCU; Subkey: "Software\Classes\.ogv\OpenWithProgids"; ValueType: string; ValueName: "BlackPearl.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\BlackPearl.exe\SupportedTypes"; ValueType: string; ValueName: ".ogv"; ValueData: ""
Root: HKCU; Subkey: "Software\BlackPearl\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ogv"; ValueData: "BlackPearl.Video"
[Run]
Filename: "{app}\BlackPearl.exe"; Description: "Open BlackPearl"; Flags: nowait postinstall skipifsilent
#endif

[Code]
// Watchroom, the app's name before BlackPearl, asks for the relaunch with WATCHROOMUPDATE when it installs this update.
function IsBlackPearlUpdate: Boolean;
begin
  Result := (ExpandConstant('{param:BLACKPEARLUPDATE|0}') = '1') or (ExpandConstant('{param:WATCHROOMUPDATE|0}') = '1');
end;

#ifdef UpdateFrom
// An update installer carries only what changed since UpdateFrom, so it refuses any other installation.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Key, Installed, Location: String;
begin
  Result := '';
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#AppGuid}}_is1';
  if not RegQueryStringValue(HKCU, Key, 'DisplayVersion', Installed) then Installed := '';
  if not RegQueryStringValue(HKCU, Key, 'InstallLocation', Location) then Location := '';
  if (Installed <> '{#UpdateFrom}') or (CompareText(AddBackslash(Location), AddBackslash(ExpandConstant('{app}'))) <> 0) then
    Result := 'This update applies to BlackPearl {#UpdateFrom} installed in this folder. Download the full installer from GitHub instead.';
end;
#endif
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode, Attempts: Integer;
  ReadyFile, FormerReadyFile: String;
  RelaunchPage: TOutputMarqueeProgressWizardPage;
begin
  if (CurStep = ssPostInstall) and IsBlackPearlUpdate then
  begin
    // The app signals from its data folder, which stays the Watchroom one for libraries made before the rename.
    ReadyFile := ExpandConstant('{localappdata}\BlackPearl\updates\ready');
    FormerReadyFile := ExpandConstant('{localappdata}\Watchroom\updates\ready');
    DeleteFile(ReadyFile);
    DeleteFile(FormerReadyFile);
    RelaunchPage := CreateOutputMarqueeProgressPage('Opening BlackPearl', 'Your update is installed. BlackPearl is starting...');
    RelaunchPage.SetText('Opening BlackPearl...', 'Your library and settings are kept.');
    RelaunchPage.Show;
    try
      if Exec(ExpandConstant('{app}\BlackPearl.exe'), '--updated', ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, ResultCode) then
      begin
        Attempts := 0;
        while (not FileExists(ReadyFile)) and (not FileExists(FormerReadyFile)) and (Attempts < 600) do
        begin
          Sleep(100);
          RelaunchPage.Animate;
          Attempts := Attempts + 1;
        end;
        DeleteFile(ReadyFile);
        DeleteFile(FormerReadyFile);
      end;
    finally
      RelaunchPage.Hide;
    end;
  end;
end;
