; Inno Setup script for the SaveLocker Windows tray agent.
;
; Machine-wide install (requests UAC elevation up front): files go to
; C:\Program Files\SaveLocker. Auto-start is still the per-user HKCU\...\Run entry
; for the installing user (matching the in-app "Start with Windows" toggle). Produces
; an Add/Remove Programs entry whose uninstaller reverts every system change: it
; removes the Run entry (even if the app created it) and offers to delete the agent's
; config/state.
;
; Build:  publish the agent first, then compile this with ISCC.exe.
;         See installer\build-installer.ps1 (does both).

#define AppName "SaveLocker"
#define AppVersion "0.1.0"
#define AppPublisher "SaveLocker"
; The binary keeps the LocalGameSync.Agent.exe filename until the project is renamed.
#define AppExe "LocalGameSync.Agent.exe"
; Path to the self-contained publish output (relative to this script).
#define PublishDir "..\src\Agent\bin\Release\net9.0-windows\win-x64\publish"
; Per-user Run key + the value name the in-app toggle (AutoStart.cs) uses.
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"
#define RunValue "SaveLocker"

[Setup]
AppId={{3BF4B1EA-D263-4136-B1F8-92800DD752D9}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
; Machine-wide install to Program Files needs admin; request elevation up front so the
; install can't fail half-way with "Access is denied" creating the program directory.
PrivilegesRequired=admin
OutputDir=dist
OutputBaseFilename=SaveLocker-Agent-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Shared with the agent's single-instance Mutex (Program.cs) so setup/uninstall can
; detect a running agent and ask the user to close it before replacing files.
AppMutex=SaveLocker.Agent
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

; Brand the wizard with SaveLocker colors.
; WizardSmallImageFile (55×58): top-right corner on all pages after Welcome.
WizardSmallImageFile=SaveLocker_WizardSmall.png
; WizardImageFile (164×314): left-panel background on Welcome and Finish pages.
WizardImageFile=SaveLocker_WizardBg.png

[Tasks]
Name: "autostart"; Description: "Start {#AppName} automatically when I log in"; GroupDescription: "Startup:"

[Files]
; Agent executable and runtime files (self-contained publish, no .NET runtime needed).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs ignoreversion
; Agent UI (React app served locally by the agent's built-in HTTP server).
; The MSBuild CopyAgentUiDistOnPublish target copies this into the publish directory.
Source: "{#PublishDir}\agent-ui\*"; DestDir: "{app}\agent-ui"; Flags: recursesubdirs ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Registry]
; Auto-start: written only if the user ticks the task; removed on uninstall. Same key
; and value the in-app toggle uses, so the two stay consistent.
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#RunValue}"; \
    ValueData: """{app}\{#AppExe}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
; runasoriginaluser: start the tray agent de-elevated as the real user, not in the
; installer's admin context (a background tray app must not run elevated).
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName} now"; \
    Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
// On uninstall: always remove the Run entry (covers the case where the in-app toggle
// created it but the install-time task was not selected), then offer to delete the
// agent's config/state (%PROGRAMDATA%\SaveLocker — holds the API key + tracked games).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, '{#RunKey}', '{#RunValue}');

  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\SaveLocker');
    if DirExists(DataDir) then
    begin
      if MsgBox('Also remove your SaveLocker settings and API key?' + #13#10 +
                '(' + DataDir + ')' + #13#10#13#10 +
                'Choose No if you plan to reinstall and keep this machine''s registration.',
                mbConfirmation, MB_YESNO) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
