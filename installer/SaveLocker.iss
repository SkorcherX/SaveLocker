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
#ifndef AppVersion
  #define AppVersion "dev"
#endif
#define AppPublisher "SaveLocker"
#define AppExe "SaveLocker.Agent.exe"
; Path to the self-contained publish output (relative to this script).
#define PublishDir "..\src\Agent\bin\Release\net10.0-windows\win-x64\publish"
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
; Sidebar background if the image doesn't fill the panel (e.g. at non-96dpi).
; Value is a Windows COLORREF (BGR): #1E252A → $2A251E.
WizardImageBackColor=$2A251E

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
    Flags: nowait postinstall runasoriginaluser

[Code]
// ---------------------------------------------------------------------------------------
// Enrollment wizard page + post-install enroll.
//
// Enrollment already works from the CLI (SaveLocker.Agent.exe enroll --file <policy>).
// This page just collects the policy file and shells out to that command; it never
// reimplements enrollment. See SaveLocker/tasks/installer-enrollment.md for the traps —
// the load-bearing ones are enforced in ShouldEnroll / DoEnroll below:
//
//   * The agent AUTO-UPDATES by running this installer /SILENT. That path must never
//     enroll: no wizard page is shown, ResolveEnrollFile returns '', and even a stray
//     /ENROLL is blocked by IsAlreadyEnrolled (re-enrolling would burn a token and
//     ROTATE this machine's key, orphaning its config).
//   * The elevated installer must NOT create %PROGRAMDATA%\SaveLocker: the de-elevated
//     tray agent has to rewrite config.json forever after. So the enroll runs via
//     ExecAsOriginalUser — the same reason [Run] uses runasoriginaluser — which creates
//     the dir as the interactive user with Modify rights.
//   * A failed enroll is non-fatal: the agent is installed and usable, just not enrolled.
//   * The token is a live credential: only the FILE PATH is ever passed or shown.
// ---------------------------------------------------------------------------------------

type
  TSysTime = record
    Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds: Word;
  end;
procedure GetSystemTime(var t: TSysTime); external 'GetSystemTime@kernel32.dll stdcall';

var
  EnrollPage: TWizardPage;
  RbEnrollNow, RbEnrollLater: TNewRadioButton;
  EdEnrollFile: TNewEdit;
  BtnBrowse: TNewButton;
  MemoPreview: TNewMemo;
  LblLater: TNewStaticText;
  EnrollPreviewOk: Boolean;

// Minimal extractor for a top-level JSON string value. The policy is flat camelCase JSON
// (Contracts.cs -> EnrollmentPolicy) whose values carry no embedded quotes, so this is
// enough for an advisory preview; the agent's enroll is the authoritative parser.
function JsonStr(const S, Key: String): String;
var
  i, j: Integer;
  k: String;
begin
  Result := '';
  k := '"' + Key + '"';
  i := Pos(k, S);
  if i = 0 then exit;
  i := i + Length(k);
  while (i <= Length(S)) and (S[i] <> ':') do Inc(i);
  Inc(i);
  while (i <= Length(S)) and ((S[i] = ' ') or (S[i] = #9) or (S[i] = #13) or (S[i] = #10)) do Inc(i);
  if (i > Length(S)) or (S[i] <> '"') then exit;  // not a string value (e.g. null / number)
  Inc(i);
  j := i;
  while (j <= Length(S)) and (S[j] <> '"') do Inc(j);
  Result := Copy(S, i, j - i);
end;

function Pad2(N: Word): String;
begin
  Result := IntToStr(N);
  if Length(Result) < 2 then Result := '0' + Result;
end;

// The server mints ExpiresAt = UtcNow.AddMinutes(ttl) (Kind=Utc -> serialized with 'Z'),
// so a lexical compare of the "YYYY-MM-DDTHH:MM:SS" prefixes against the current UTC time
// is a correct expiry check. Best-effort: if the field is missing/short, defer to the
// agent, which prints the authoritative "expired ... mint a new one" message.
function PolicyExpired(const ExpiresAt: String): Boolean;
var
  st: TSysTime;
  nowIso: String;
begin
  Result := False;
  if Length(ExpiresAt) < 19 then exit;
  GetSystemTime(st);
  nowIso := IntToStr(st.Year) + '-' + Pad2(st.Month) + '-' + Pad2(st.Day) + 'T' +
            Pad2(st.Hour) + ':' + Pad2(st.Minute) + ':' + Pad2(st.Second);
  Result := Copy(ExpiresAt, 1, 19) < nowIso;
end;

// "2026-07-14T12:34:56Z" -> "2026-07-14 12:34" for display.
function FriendlyUtc(const ExpiresAt: String): String;
begin
  if Length(ExpiresAt) < 16 then
    Result := ExpiresAt
  else
  begin
    Result := Copy(ExpiresAt, 1, 16);
    StringChangeEx(Result, 'T', ' ', True);
  end;
end;

procedure RefreshPreview;
var
  s: AnsiString;
  url, token, name, exp: String;
begin
  EnrollPreviewOk := False;
  if (RbEnrollNow = nil) or (not RbEnrollNow.Checked) then
  begin
    MemoPreview.Text := '';
    exit;
  end;
  if Trim(EdEnrollFile.Text) = '' then
  begin
    MemoPreview.Text := 'Choose the enrollment file you downloaded from the console' + #13#10 +
                        '(Configuration -> Enroll a machine).';
    exit;
  end;
  if not FileExists(EdEnrollFile.Text) then
  begin
    MemoPreview.Text := 'File not found:' + #13#10 + EdEnrollFile.Text;
    exit;
  end;
  if not LoadStringFromFile(EdEnrollFile.Text, s) then
  begin
    MemoPreview.Text := 'Could not read the file.';
    exit;
  end;
  url   := JsonStr(s, 'serverUrl');
  token := JsonStr(s, 'token');
  name  := JsonStr(s, 'machineName');
  exp   := JsonStr(s, 'expiresAt');
  if (url = '') or (token = '') then
  begin
    MemoPreview.Text := 'This does not look like a SaveLocker enrollment file.';
    exit;
  end;
  if PolicyExpired(exp) then
  begin
    MemoPreview.Text := 'This enrollment file expired at ' + FriendlyUtc(exp) + ' UTC.' + #13#10 +
                        'Create a new one from the console, or choose "Skip" below.';
    exit;
  end;
  // The policy is deliberately unsigned: the user is the trust anchor. Show the server so
  // they can catch a file that points at the wrong (or a malicious) server before joining.
  MemoPreview.Text :=
    'You are about to join:' + #13#10 +
    '    Server:   ' + url + #13#10;
  if name <> '' then
    MemoPreview.Text := MemoPreview.Text + '    As machine:   ' + name + #13#10
  else
    MemoPreview.Text := MemoPreview.Text + '    Machine name:   (this computer''s name)' + #13#10;
  if exp <> '' then
    MemoPreview.Text := MemoPreview.Text + '    Expires:   ' + FriendlyUtc(exp) + ' UTC' + #13#10;
  MemoPreview.Text := MemoPreview.Text + #13#10 +
    'Make sure the server address is one you trust before continuing.';
  EnrollPreviewOk := True;
end;

procedure EnrollOptionClicked(Sender: TObject);
var
  useNow: Boolean;
begin
  useNow := RbEnrollNow.Checked;
  EdEnrollFile.Enabled := useNow;
  BtnBrowse.Enabled := useNow;
  RefreshPreview;
end;

procedure BtnBrowseClick(Sender: TObject);
var
  f: String;
begin
  f := EdEnrollFile.Text;
  if GetOpenFileName('Select your SaveLocker enrollment file', f, '',
       'Enrollment files (*.json)|*.json|All files (*.*)|*.*', 'json') then
  begin
    RbEnrollNow.Checked := True;
    EdEnrollFile.Text := f;
    EnrollOptionClicked(nil);
  end;
end;

procedure InitializeWizard;
begin
  EnrollPage := CreateCustomPage(wpSelectDir, 'Enroll this machine',
    'Connect this machine to your SaveLocker server now, or do it later.');

  RbEnrollNow := TNewRadioButton.Create(WizardForm);
  RbEnrollNow.Parent := EnrollPage.Surface;
  RbEnrollNow.Left := 0;
  RbEnrollNow.Top := ScaleY(8);
  RbEnrollNow.Width := EnrollPage.SurfaceWidth;
  RbEnrollNow.Caption := 'Enroll this machine now (recommended)';
  RbEnrollNow.Checked := True;
  RbEnrollNow.OnClick := @EnrollOptionClicked;

  EdEnrollFile := TNewEdit.Create(WizardForm);
  EdEnrollFile.Parent := EnrollPage.Surface;
  EdEnrollFile.Left := ScaleX(20);
  EdEnrollFile.Top := ScaleY(34);
  EdEnrollFile.Width := EnrollPage.SurfaceWidth - ScaleX(20) - ScaleX(90);
  EdEnrollFile.Height := ScaleY(23);

  BtnBrowse := TNewButton.Create(WizardForm);
  BtnBrowse.Parent := EnrollPage.Surface;
  BtnBrowse.Left := EnrollPage.SurfaceWidth - ScaleX(84);
  BtnBrowse.Top := ScaleY(33);
  BtnBrowse.Width := ScaleX(84);
  BtnBrowse.Height := ScaleY(25);
  BtnBrowse.Caption := 'Browse...';
  BtnBrowse.OnClick := @BtnBrowseClick;

  MemoPreview := TNewMemo.Create(WizardForm);
  MemoPreview.Parent := EnrollPage.Surface;
  MemoPreview.Left := ScaleX(20);
  MemoPreview.Top := ScaleY(64);
  MemoPreview.Width := EnrollPage.SurfaceWidth - ScaleX(20);
  MemoPreview.Height := ScaleY(96);
  MemoPreview.ReadOnly := True;
  MemoPreview.ScrollBars := ssVertical;
  MemoPreview.WantReturns := False;
  MemoPreview.TabStop := False;

  RbEnrollLater := TNewRadioButton.Create(WizardForm);
  RbEnrollLater.Parent := EnrollPage.Surface;
  RbEnrollLater.Left := 0;
  RbEnrollLater.Top := ScaleY(172);
  RbEnrollLater.Width := EnrollPage.SurfaceWidth;
  RbEnrollLater.Caption := 'Skip - I''ll enroll later';
  RbEnrollLater.OnClick := @EnrollOptionClicked;

  LblLater := TNewStaticText.Create(WizardForm);
  LblLater.Parent := EnrollPage.Surface;
  LblLater.Left := ScaleX(20);
  LblLater.Top := ScaleY(192);
  LblLater.Width := EnrollPage.SurfaceWidth - ScaleX(20);
  LblLater.AutoSize := False;
  LblLater.WordWrap := True;
  LblLater.Height := ScaleY(32);
  LblLater.Caption := 'You can enroll anytime from the agent''s Settings tab, or by running ' +
                      'SaveLocker.Agent.exe enroll --file <file>.';

  EnrollOptionClicked(nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (EnrollPage <> nil) and (CurPageID = EnrollPage.ID) and RbEnrollNow.Checked then
  begin
    if Trim(EdEnrollFile.Text) = '' then
    begin
      MsgBox('Choose an enrollment file, or select "Skip - I''ll enroll later".',
             mbError, MB_OK);
      Result := False;
      exit;
    end;
    if not EnrollPreviewOk then
    begin
      MsgBox(MemoPreview.Text + #13#10 + #13#10 +
             'Fix the file or choose "Skip - I''ll enroll later".', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

// The file to enroll with: the wizard's choice interactively, else the /ENROLL switch
// (scripted/unattended). A silent auto-update passes neither -> '' -> no enroll.
function ResolveEnrollFile: String;
begin
  if (not WizardSilent) and (EnrollPage <> nil) then
  begin
    if RbEnrollNow.Checked then
      Result := Trim(EdEnrollFile.Text)
    else
      Result := '';
  end
  else
    Result := ExpandConstant('{param:ENROLL|}');
end;

// True once this machine holds an API key. config.json is written with WhenWritingNull,
// so the "ApiKey" property is present only when set -> a reliable "already enrolled" guard.
function IsAlreadyEnrolled: Boolean;
var
  cfg: String;
  s: AnsiString;
begin
  Result := False;
  cfg := ExpandConstant('{commonappdata}\SaveLocker\config.json');
  if FileExists(cfg) and LoadStringFromFile(cfg, s) then
    Result := Pos('"ApiKey"', s) > 0;
end;

procedure DoEnroll;
var
  f, exe: String;
  rc: Integer;
begin
  f := ResolveEnrollFile;
  if f = '' then exit;                 // skip / silent auto-update / nothing chosen
  if not FileExists(f) then exit;
  if IsAlreadyEnrolled then exit;      // never re-enroll: it burns a token + rotates the key

  exe := ExpandConstant('{app}\{#AppExe}');
  // De-elevated, so %PROGRAMDATA%\SaveLocker is created by (and stays writable to) the
  // user the tray runs as. Only the file path crosses the command line, never the token.
  if ExecAsOriginalUser(exe, 'enroll --file "' + f + '"', '', SW_HIDE,
                        ewWaitUntilTerminated, rc) then
  begin
    if (rc <> 0) and (not WizardSilent) then
      MsgBox('SaveLocker was installed, but enrolling this machine did not complete.'#13#10#13#10 +
             'You can enroll from the agent''s Settings tab, or run:'#13#10 +
             '    "' + exe + '" enroll --file <file>'#13#10#13#10 +
             'A common cause is an expired file - create a new one from the console.',
             mbInformation, MB_OK);
  end
  else if not WizardSilent then
    MsgBox('SaveLocker was installed, but the enrollment step could not be started.'#13#10 +
           'You can enroll later from the agent''s Settings tab.',
           mbInformation, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // ssPostInstall runs after files are in place but before the [Run] tray launch, so the
  // agent comes up already enrolled. Enrollment failures never abort the install.
  if CurStep = ssPostInstall then
    DoEnroll;
end;

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
