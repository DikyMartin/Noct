; Inno Setup script for Noct. Build it with build\build-installer.ps1, which publishes the app and
; passes the version in: ISCC.exe /DAppVersion=0.5.0 /DSourceDir=<publish folder> installer\Noct.iss
;
; Installs for the current user only (no administrator rights). User data lives in %LocalAppData%\Noct
; and is never part of the installed files.

#ifndef AppVersion
  #error AppVersion must be passed on the command line (/DAppVersion=x.y.z).
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\Noct"
#endif

#define AppName "Noct"
#define AppExe "Noct.exe"

[Setup]
; Never change this value: it is how a newer installer finds and updates an existing installation.
AppId={{244C66B4-07CE-415C-AF8B-BD870EB3DCD8}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Noct
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
DefaultDirName={localappdata}\Programs\Noct
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\dist\installer
OutputBaseFilename=Noct-Setup-{#AppVersion}-x64
SetupIconFile=..\src\Noct.App\Assets\Noct.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Running copies are closed by the code below (graceful first), not by Restart Manager.
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[InstallDelete]
; An update replaces the program folder wholesale so files dropped from a newer build do not linger.
; This is the install folder only; settings, library and artwork cache are in %LocalAppData%\Noct.
Type: filesandordirs; Name: "{app}\*"

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; Comment: "Personal local FLAC player"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  // Noct's single-instance mutex (see SingleInstance.cs). It exists exactly while Noct is running.
  NoctMutex = 'Local\Noct.SingleInstance.v1';

function NoctIsRunning: Boolean;
begin
  Result := CheckForMutexes(NoctMutex);
end;

// Waits up to Milliseconds for Noct to exit. Returns True when it is gone.
function WaitForNoctToExit(Milliseconds: Integer): Boolean;
var
  Waited: Integer;
begin
  Waited := 0;
  while NoctIsRunning and (Waited < Milliseconds) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  Result := not NoctIsRunning;
end;

// Closes a running Noct, politely first, so it stops playback, saves its settings and releases the audio
// device. A normal close request only reaches a visible window, so when Noct is hidden in the island or the
// tray it is first asked to show its window (starting Noct again does that, see SingleInstance.cs), then
// closed. Only if it is still there after that is it ended.
procedure CloseRunningNoct;
var
  ResultCode: Integer;
  Exe: String;
begin
  if not NoctIsRunning then
    Exit;

  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if WaitForNoctToExit(3000) then
    Exit;

  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
  begin
    Exec(Exe, '', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode);
    Sleep(1500);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if WaitForNoctToExit(10000) then
      Exit;
  end;

  Log('Noct did not close on request; ending it.');
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
end;
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  WizardForm.PreparingLabel.Caption := 'Closing Noct...';
  CloseRunningNoct;
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  CloseRunningNoct;
  Result := True;
end;

// Uninstalling removes the program only. Settings, the library index and the artwork cache stay in
// %LocalAppData%\Noct unless the user says otherwise. Music and .lrc files are never touched: they are
// not in either folder. A silent uninstall keeps the data unless it is started with /PURGEDATA=1.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  Purge: Boolean;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  DataDir := ExpandConstant('{localappdata}\Noct');
  if not DirExists(DataDir) then
    Exit;

  Purge := ExpandConstant('{param:PURGEDATA|0}') = '1';
  if (not Purge) and (not UninstallSilent) then
    Purge := MsgBox('Also remove your Noct settings, library index and artwork cache?' + #13#10 + #13#10 +
      DataDir + #13#10 + #13#10 +
      'Your music and lyrics files are not affected either way. Choose No to keep your settings for a later reinstall.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;

  if Purge then
    DelTree(DataDir, True, True, True);
end;
