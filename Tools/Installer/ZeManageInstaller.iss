#define MyAppName "ZeManage"
#define MyAppVersion "1.0.1"
#define MyAppPublisher "ZestineTech"

; Repo root: parent of Tools/ (which is parent of Installer/). ExtractFilePath
; returns the path WITH a trailing backslash, so RepoRoot already ends in `\`.
#define RepoRoot ExtractFilePath(RemoveBackslash(ExtractFilePath(RemoveBackslash(SourcePath))))

; Bundle root: Bundle\BIManageRevit.bundle\Contents\<year>\ is the canonical
; signed payload. Pipeline:
;   01_OrgUpdate     -> build + stage + trim
;   02_Obfuscate     -> Obfuscar string-hide -> Confused\<year>\
;   03_BundleUpdate  -> overlay obfuscated DLLs into publish AND assemble
;                       Bundle\BIManageRevit.bundle\Contents\<year>\
;   04_SignTool      -> sign DLLs in the BUNDLE, then mirror signed copies
;                       back to publish
;   06_Installer     -> THIS file. Was previously sourcing from publish/...
;                       but the bundle is structurally identical, is the
;                       signed source-of-truth, and matches what the
;                       Application Plugin Manager install path ships -
;                       so using it here keeps the two install routes
;                       byte-identical.
#define BundleRoot RepoRoot + "Bundle\BIManageRevit.bundle\Contents"

; Agent EXE — self-contained win-x64 publish (bundles .NET 10 runtime, works on any Windows machine)
; To rebuild: dotnet publish ZeManage.Agent/ZeManage.Agent.csproj -c Release -r win-x64 --self-contained true -o ZeManage.Agent/publish/win-x64
#define AgentSource RepoRoot + "ZeManage.Agent\publish\win-x64"
#if !FileExists(AgentSource + "\ZeManage.Agent.exe")
  #error "Agent not published. Run: dotnet publish ZeManage.Agent/ZeManage.Agent.csproj -c Release -r win-x64 --self-contained true -o ZeManage.Agent/publish/win-x64"
#endif

#define ApiBaseUrl "http://10.10.40.75:5000"
#define UninstallEndpoint ApiBaseUrl + "/api/v1/Tenant/devices/uninstall"

[Setup]
AppId={{C1E4C6A3-8E6B-4F4B-8D7A-ZEMANAGE001}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}

DefaultDirName={autopf}\ZestineTech\ZeManage
DisableWelcomePage=no
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes

OutputDir=Output
OutputBaseFilename=ZeManageSetup

Compression=lzma
SolidCompression=yes
SetupLogging=yes

WizardStyle=modern
WizardImageFile=Zestine.bmp
; WizardSmallImageFile removed for cleaner header
SetupIconFile=BIManage.ico
WizardSizePercent=120,100
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\BIManage.ico
UninstallDisplayName={#MyAppName} for Revit
SetupMutex=ZeManageSetupMutex
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
ExitSetupMessage=ZeManage setup is not complete. If you exit now, ZeManage will not be installed.%n%nExit Setup?
ConfirmUninstall=Are you sure you want to completely remove ZeManage from your computer?
UninstallStatusLabel=Please wait while ZeManage is being removed from your computer...

[Files]

; App icon for uninstaller display
Source: "BIManage.ico"; DestDir: "{app}"; Flags: ignoreversion

; ==========================================================================
; Revit version files - generated via ISPP loop
; I=0..3 -> R21-R24 (net48), I=4..5 -> R25-R26 (net8.0), I=6 -> R27 (net10.0)
; ==========================================================================
#define I

#sub EmitVersionFiles
  #define VS "R" + Str(21 + I)
  #define VY Str(2021 + I)
  ; Per-year bundle folder. Layout mirrors what the Application Plugin Manager
  ; bundle ships under %PROGRAMDATA%\Autodesk\ApplicationPlugins\BIManageRevit.bundle\:
  ;   {#VP}\BIManageRevit.addin           <- manifest at the year root
  ;   {#VP}\BIManageRevit\<dll/deps>      <- DLLs + dependencies
  #define VP BundleRoot + "\" + VY

#if FileExists(VP + "\BIManageRevit\BIManageRevit.dll")
; ---- Revit {#VY} ({#VS}) ----
; .addin manifest at the Addins root
Source: "{#VP}\BIManageRevit.addin"; \
  DestDir: "{code:GetAddinsDir|{#VY}}"; \
  Flags: ignoreversion; Check: Install{#VS}

; Recursive copy of everything in the per-year bundle EXCEPT BIManage.Addons.dll
; (which is gated behind a separate user checkbox below). Wildcard avoids the brittle
; per-file enumeration that broke when new NuGet deps (CommunityToolkit, MaterialDesign,
; Nice3point) or moved files (Newtonsoft -> lib/) weren't manually added here.
Source: "{#VP}\BIManageRevit\*"; \
  Excludes: "BIManage.Addons.dll"; \
  DestDir: "{code:GetAddinsDir|{#VY}}\BIManageRevit"; \
  Flags: ignoreversion recursesubdirs createallsubdirs; Check: Install{#VS}

; Addon (conditional on user checkbox — not in the main wildcard above)
Source: "{#VP}\BIManageRevit\BIManage.Addons.dll"; \
  DestDir: "{code:GetAddinsDir|{#VY}}\BIManageRevit"; \
  Flags: ignoreversion skipifsourcedoesntexist; Check: InstallAddon{#VS}

#endif
#endsub

#for {I = 0; I < 7; I++} EmitVersionFiles

; ==========================================================================
; ZeManage Agent EXE — installed to {app}\Agent\
; Gated by AgentInstallEnabled (register-or-validate-device's "remoteControl" flag) — when
; False, this is a Revit-only install: no Agent files, no autostart, no local capture.
; ==========================================================================
Source: "{#AgentSource}\*"; \
  DestDir: "{app}\Agent"; \
  Flags: ignoreversion recursesubdirs createallsubdirs; \
  Excludes: "*.pdb"; \
  Check: AgentShouldInstall

; appsettings.json with localhost backend URL (written by [Code] below, not a static file)


[Icons]
; Start Menu shortcut — appears in Windows search when user types "ZeManage"
Name: "{autoprograms}\ZeManage"; \
  Filename: "{app}\Agent\ZeManage.Agent.exe"; \
  IconFilename: "{app}\BIManage.ico"; \
  Comment: "ZeManage Agent - click to start if not running"; \
  Check: AgentShouldInstall


[Registry]
; Register Agent in Windows startup so it runs on every login (per-user, no elevation needed)
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "ZeManageAgent"; \
  ValueData: """{app}\Agent\ZeManage.Agent.exe"" --minimized"; \
  Flags: uninsdeletevalue; \
  Check: AgentShouldInstall
; Watchdog: relaunches the Agent if it's ever terminated. The Agent also self-launches it on its
; own startup (belt-and-suspenders — see App.xaml.cs LaunchWatchdogIfNotRunning), so this entry
; mainly covers the case where the watchdog alone was killed while the Agent kept running.
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "ZeManageAgentWatchdog"; \
  ValueData: """{app}\Agent\ZeManage.Agent.Watchdog.exe"""; \
  Flags: uninsdeletevalue; \
  Check: AgentShouldInstall


[UninstallRun]
; Stop Agent + Watchdog before uninstall so files can be deleted. Watchdog first — otherwise it
; would just relaunch the Agent a few seconds after this taskkill.
Filename: "taskkill.exe"; \
  Parameters: "/F /IM ZeManage.Agent.Watchdog.exe"; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "StopAgentWatchdog"
Filename: "taskkill.exe"; \
  Parameters: "/F /IM ZeManage.Agent.exe"; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "StopAgent"


[Code]

procedure ExitProcess(ExitCode: Cardinal);
  external 'ExitProcess@kernel32.dll stdcall';

function OpenEventW(dwDesiredAccess: LongWord; bInheritHandle: BOOL; lpName: String): LongWord;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: LongWord): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(hObject: LongWord): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

const
  EVENT_MODIFY_STATE = $0002;

// Asks a running ZeManage.Agent/.Watchdog process to exit itself (App.xaml.cs / Program.cs both
// listen for this named event) — works even when this installer itself isn't elevated, since a
// process exiting on its own needs no PROCESS_TERMINATE handle rights. The taskkill fallback in
// [UninstallRun]/CurStepChanged only actually succeeds when this installer IS elevated, per
// ProcessProtection's DACL on the target process — this is the primary path, that's the fallback.
procedure SignalGracefulExit(const EventName: String);
var
  hEvent: LongWord;
begin
  hEvent := OpenEventW(EVENT_MODIFY_STATE, False, EventName);
  if hEvent <> 0 then
  begin
    SetEvent(hEvent);
    CloseHandle(hEvent);
    Log('[ZeManage] Sent graceful-exit signal: ' + EventName);
  end;
end;

var
  LicensePage: TWizardPage;
  InstallPage: TWizardPage;

  LicenseEdit: TNewEdit;
  LicenseStatus: TNewStaticText;

  R21,R22,R23,R24,R25,R26,R27: TNewCheckBox;
  AddonCheck: TNewCheckBox;

  InstallButton: TNewButton;
  PrivacyCheck: TNewCheckBox;
  PrivacyLink: TNewStaticText;

  LicenseValidated: Boolean;
  PrivacyAccepted: Boolean;
  ValidationCancelled: Boolean;
  UninstallCompleted: Boolean;
  LastValidationMessage: String;

  // From register-or-validate-device's "remoteControl" flag — gates whether the Agent
  // (tray app + watchdog + local capture) gets installed at all. False means Revit-only:
  // no Agent files, no autostart registration, no local data capture on this machine.
  // Defaults True (install Agent) so a missing/unparseable flag preserves prior behavior.
  AgentInstallEnabled: Boolean;

  // Comma-wrapped list of Revit years with a running Revit.exe, e.g. ",2024,2025,"
  RunningRevitVersions: String;
  // Set to True when one or more selected versions were skipped due to running Revit
  PartialInstall: Boolean;

  MaintenancePage: TWizardPage;
  MaintenanceUpdateRadio: TNewRadioButton;
  MaintenanceUninstallRadio: TNewRadioButton;
  IsExistingInstall: Boolean;
  ExistingUninstallString: String;
  // Second uninstaller path when an install exists in BOTH HKLM (admin) and HKCU (per-user).
  // Visible to the user as a duplicate entry in Programs & Features. Both must be uninstalled.
  SecondUninstallString: String;



// ==========================================================================
// Silent install support
// Usage: ZeManageSetup.exe /VERYSILENT /LICENSE=your-key /VERSIONS=R25,R26,R27 /ADDON=1
//   /LICENSE   - Required. License key to validate.
//   /VERSIONS  - Optional. Comma-separated: R21,R22,R23,R24,R25,R26,R27
//                If omitted, auto-detects installed Revit versions.
//   /ADDON     - Optional. Set to 1 to include BIManage.Addons.dll.
//   /CURRENTUSER - Optional. Install for current user only.
// ==========================================================================

function GetAddinsDir(Param: String): String;
begin
  if IsAdminInstallMode then
    Result := ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\' + Param)
  else
  begin
    // SYSTEM account (ManageEngine/SCCM) has no user profile — fall back to ProgramData
    if ExpandConstant('{username}') = 'SYSTEM' then
      Result := ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\' + Param)
    else
      Result := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\' + Param);
  end;
end;

function GetUserAddinsDir(Year: String): String;
begin
  // SYSTEM account has no user profile — {userappdata} will fail
  if ExpandConstant('{username}') = 'SYSTEM' then
    Result := ''
  else
    Result := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\' + Year);
end;

function GetCommonAddinsDir(Year: String): String;
begin
  Result := ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\' + Year);
end;

/// Removes BIManageRevit addin + folder from a specific Addins directory.
/// Called to clean the OTHER location (prevents duplicate plugin loading).
procedure CleanAddinFromDir(BaseDir: String);
var
  AddinFile, PluginDir: String;
begin
  if BaseDir = '' then Exit;
  AddinFile := BaseDir + '\BIManageRevit.addin';
  PluginDir := BaseDir + '\BIManageRevit';
  if FileExists(AddinFile) then
  begin
    Log('[ZeManage] Removing duplicate addin: ' + AddinFile);
    DeleteFile(AddinFile);
  end;
  if DirExists(PluginDir) then
  begin
    Log('[ZeManage] Removing duplicate plugin folder: ' + PluginDir);
    DelTree(PluginDir, True, True, True);
  end;
end;

/// Removes BIManageRevit from BOTH user and common Addins folders for a given year.
procedure CleanAddinFromBothLocations(Year: String);
begin
  CleanAddinFromDir(GetUserAddinsDir(Year));
  CleanAddinFromDir(GetCommonAddinsDir(Year));
end;

/// Enumerates all user profiles and cleans addin files + DB from each.
/// Needed when uninstaller runs as SYSTEM (e.g. ManageEngine) since
/// {userappdata}/{localappdata} resolve to SYSTEM's folders, not real users.
/// PreserveDb=True: skip DB and auth-token deletion (upgrade path).
procedure CleanAllUserProfiles(PreserveDb: Boolean);
var
  Years: array of String;
  I: Integer;
  FindRec: TFindRec;
  UsersDir, UserProfile, UserAppData, UserLocalAppData: String;
  AddinBase, DbPath: String;
begin
  SetArrayLength(Years, 7);
  Years[0] := '2021'; Years[1] := '2022'; Years[2] := '2023';
  Years[3] := '2024'; Years[4] := '2025'; Years[5] := '2026';
  Years[6] := '2027';

  UsersDir := ExpandConstant('{sd}\Users');
  Log('[ZeManage] Enumerating user profiles in: ' + UsersDir +
      ' (PreserveDb=' + IntToStr(Ord(PreserveDb)) + ')');

  if FindFirst(UsersDir + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          if (FindRec.Name <> '.') and (FindRec.Name <> '..') and
             (FindRec.Name <> 'Public') and (FindRec.Name <> 'Default') and
             (FindRec.Name <> 'Default User') and (FindRec.Name <> 'All Users') then
          begin
            UserProfile := UsersDir + '\' + FindRec.Name;
            UserAppData := UserProfile + '\AppData\Roaming';
            UserLocalAppData := UserProfile + '\AppData\Local';

            // Clean addin files for each Revit version
            for I := 0 to 6 do
            begin
              AddinBase := UserAppData + '\Autodesk\Revit\Addins\' + Years[I];
              CleanAddinFromDir(AddinBase);
            end;

            // Also clean from ProgramData (common) location
            for I := 0 to 6 do
              CleanAddinFromDir(GetCommonAddinsDir(Years[I]));

            // Remove SQLite database — skip on upgrade
            if not PreserveDb then
            begin
              DbPath := UserLocalAppData + '\BIManageRevit\Logs\bimanage.db';
              if FileExists(DbPath) then
              begin
                if DeleteFile(DbPath) then
                  Log('[ZeManage] Deleted database for user ' + FindRec.Name)
                else
                  Log('[ZeManage] Could not delete database for user ' + FindRec.Name);
              end;

              // Remove auth tokens (DPAPI-protected refresh token)
              if DeleteFile(UserAppData + '\BIManage\auth.dat') then
                Log('[ZeManage] Deleted auth tokens for user ' + FindRec.Name);
            end;
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

/// Wipes BIManageRevit from BOTH user (AppData) and common (ProgramData) Addins
/// folders before re-installing. Critical because:
///   1. Nice3point's DeployRevitAddin (build-time) drops a copy in AppData on dev machines
///   2. A previous install of the opposite mode leaves a copy in the other location
/// If both locations have BIManageRevit.addin with the same AddInId, Revit fails
/// to load the plugin (silent duplicate registration conflict).
/// We only KEEP the location matching the current install mode — the [Files] section
/// then re-creates the correct one fresh.
procedure CleanDuplicateAddins();
var
  Years: array of String;
  I: Integer;
  KeepDir: String;
begin
  SetArrayLength(Years, 7);
  Years[0] := '2021'; Years[1] := '2022'; Years[2] := '2023';
  Years[3] := '2024'; Years[4] := '2025'; Years[5] := '2026';
  Years[6] := '2027';

  for I := 0 to 6 do
  begin
    // Determine which dir the [Files] section is about to write into
    KeepDir := GetAddinsDir(Years[I]);

    // Always clean the opposite location
    if IsAdminInstallMode then
      CleanAddinFromDir(GetUserAddinsDir(Years[I]))
    else
      CleanAddinFromDir(GetCommonAddinsDir(Years[I]));

    // Also clean the SAME location if a stale build-time deploy is sitting there.
    // The [Files] section runs after this and re-creates fresh files.
    CleanAddinFromDir(KeepDir);
  end;
end;


function QueryWMI(const WQL, PropName: String): String;
var
  Locator, Service, Items, Item: Variant;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\cimv2');
    Items := Service.ExecQuery(WQL);
    if not VarIsNull(Items) then
      if Items.Count > 0 then
      begin
        Item := Items.ItemIndex(0);
        Result := Item.Properties_(PropName).Value;
      end;
  except
    Result := '';
  end;
end;


function MD5ToGuid(const Hash: String): String;
begin
  // Replicate C# new Guid(byte[]) byte ordering (little-endian for first 3 groups)
  Result := Copy(Hash, 7, 2) + Copy(Hash, 5, 2) + Copy(Hash, 3, 2) + Copy(Hash, 1, 2) + '-' +
            Copy(Hash, 11, 2) + Copy(Hash, 9, 2) + '-' +
            Copy(Hash, 15, 2) + Copy(Hash, 13, 2) + '-' +
            Copy(Hash, 17, 4) + '-' +
            Copy(Hash, 21, 12);
end;


function GetWmicValue(Args: String): String;
var
  ResultCode: Integer;
  TmpFile: String;
  Lines: TArrayOfString;
begin
  Result := '';
  TmpFile := ExpandConstant('{tmp}\wmic_out.txt');
  Exec('cmd.exe', '/C wmic ' + Args + ' > "' + TmpFile + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if LoadStringsFromFile(TmpFile, Lines) then
  begin
    if GetArrayLength(Lines) >= 2 then
      Result := Trim(Lines[1]);
  end;
  DeleteFile(TmpFile);
end;


function GetCurrentUserSid(): String;
var
  ResultCode: Integer;
  TmpFile, ScriptFile: String;
  Lines: TArrayOfString;
begin
  Result := '';
  TmpFile   := ExpandConstant('{tmp}\usersid.txt');
  ScriptFile := ExpandConstant('{tmp}\usersid.ps1');
  SaveStringToFile(ScriptFile,
    'try { $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value; ' +
    '[IO.File]::WriteAllText("' + TmpFile + '", $sid) } catch { }',
    False);
  Exec('powershell.exe',
       '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptFile + '"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  DeleteFile(ScriptFile);
  if LoadStringsFromFile(TmpFile, Lines) and (GetArrayLength(Lines) > 0) then
    Result := Trim(Lines[0]);
  DeleteFile(TmpFile);
end;


function GetMachineId(): String;
var
  ResultCode: Integer;
  TmpFile: String;
  Lines: TArrayOfString;
  Script, ScriptFile: String;
begin
  // Use PowerShell to generate machine ID with EXACT same logic as C# MachineIdentifier.cs:
  //   1. Read MachineGuid from registry
  //   2. Get motherboard serial + processor ID via wmic
  //   3. Combine as "{MachineGuid}|{MB}|{CPU}"
  //   4. MD5 hash → new Guid(byte[]) → ToString()
  // This guarantees the installer and Revit app produce identical machine IDs.
  TmpFile := ExpandConstant('{tmp}\machineid.txt');
  ScriptFile := ExpandConstant('{tmp}\machineid.ps1');

  // Replicate C# MachineIdentifier.GetWmicValue() exactly:
  //   var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
  //   if (lines.Length >= 2) { value = lines[1].Trim(); if (value == "None") value = ""; }
  Script :=
    'function Get-WmicVal($args_) {' + #13#10 +
    '  try {' + #13#10 +
    '    $out = & wmic $args_.Split(" ") 2>$null | Out-String' + #13#10 +
    '    $lines = $out -split "[\r\n]" | Where-Object { $_ -ne "" }' + #13#10 +
    '    if ($lines.Count -ge 2) {' + #13#10 +
    '      $v = $lines[1].Trim()' + #13#10 +
    '      if ($v -and $v -ne "None") { return $v }' + #13#10 +
    '    }' + #13#10 +
    '  } catch { }' + #13#10 +
    '  return ""' + #13#10 +
    '}' + #13#10 +
    'try {' + #13#10 +
    '  $mg = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Cryptography" -Name MachineGuid -EA Stop).MachineGuid' + #13#10 +
    '} catch { $mg = "" }' + #13#10 +
    '$mb = Get-WmicVal "baseboard get SerialNumber"' + #13#10 +
    '$cpu = Get-WmicVal "cpu get ProcessorId"' + #13#10 +
    '$combined = "$mg|$mb|$cpu"' + #13#10 +
    '$md5 = [System.Security.Cryptography.MD5]::Create()' + #13#10 +
    '$hash = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($combined))' + #13#10 +
    '$guid = (New-Object Guid (,$hash)).ToString()' + #13#10 +
    '[System.IO.File]::WriteAllText("' + TmpFile + '", "$guid|$mg|$mb|$cpu")';

  SaveStringToFile(ScriptFile, Script, False);
  Exec('powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptFile + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  DeleteFile(ScriptFile);

  if LoadStringsFromFile(TmpFile, Lines) and (GetArrayLength(Lines) > 0) then
  begin
    // Output format: "guid|machineGuid|mb|cpu"
    // Extract GUID (first 36 chars)
    Result := Copy(Lines[0], 1, 36);
    Log('[ZeManage] Hardware IDs: ' + Lines[0]);
  end
  else
  begin
    // Fallback: use computer name + user name (matches C# fallback)
    Log('[ZeManage] PowerShell machine ID failed, using fallback');
    Script :=
      '$combined = "' + ExpandConstant('{computername}') + '|' + ExpandConstant('{username}') + '"' + #13#10 +
      '$md5 = [System.Security.Cryptography.MD5]::Create()' + #13#10 +
      '$hash = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($combined))' + #13#10 +
      '$guid = (New-Object Guid (,$hash)).ToString()' + #13#10 +
      '[System.IO.File]::WriteAllText("' + TmpFile + '", $guid)';

    ScriptFile := ExpandConstant('{tmp}\machineid_fb.ps1');
    SaveStringToFile(ScriptFile, Script, False);
    Exec('powershell.exe',
      '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptFile + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    DeleteFile(ScriptFile);

    if LoadStringsFromFile(TmpFile, Lines) and (GetArrayLength(Lines) > 0) then
      Result := Copy(Lines[0], 1, 36)
    else
      Result := '00000000-0000-0000-0000-000000000000';

    Log('[ZeManage] Using fallback machine ID: ' + Result);
  end;

  DeleteFile(TmpFile);
end;


// ==========================================================================
// Maintenance mode: detect existing installation
// ==========================================================================

function IsAlreadyInstalled(): Boolean;
var
  UninstStr: String;
  HklmStr, HkcuStr: String;
begin
  Result := False;
  ExistingUninstallString := '';
  SecondUninstallString := '';
  HklmStr := '';
  HkcuStr := '';

  // Detect BOTH hives. A particular system can end up with admin AND per-user
  // installs simultaneously (shows as a duplicate entry in Programs & Features).
  if RegQueryStringValue(HKLM,
    'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{C1E4C6A3-8E6B-4F4B-8D7A-ZEMANAGE001}}_is1',
    'UninstallString', UninstStr) then
  begin
    HklmStr := RemoveQuotes(UninstStr);
    Log('[ZeManage] Existing install found (HKLM): ' + HklmStr);
  end;

  if RegQueryStringValue(HKCU,
    'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{C1E4C6A3-8E6B-4F4B-8D7A-ZEMANAGE001}}_is1',
    'UninstallString', UninstStr) then
  begin
    HkcuStr := RemoveQuotes(UninstStr);
    Log('[ZeManage] Existing install found (HKCU): ' + HkcuStr);
  end;

  if (HklmStr <> '') and (HkcuStr <> '') then
  begin
    Log('[ZeManage] DUPLICATE INSTALL DETECTED — both HKLM and HKCU registrations exist. Both will be removed.');
    ExistingUninstallString := HklmStr;
    SecondUninstallString := HkcuStr;
    Result := True;
  end
  else if HklmStr <> '' then
  begin
    ExistingUninstallString := HklmStr;
    Result := True;
  end
  else if HkcuStr <> '' then
  begin
    ExistingUninstallString := HkcuStr;
    Result := True;
  end;
end;

/// Invokes a single uninstaller executable with the right silent flags.
function InvokeUninstaller(UninstallerPath: String; PreserveDb: Boolean): Boolean;
var
  ResultCode: Integer;
  Params: String;
begin
  Result := False;
  if UninstallerPath = '' then Exit;
  if not FileExists(UninstallerPath) then
  begin
    Log('[ZeManage] Uninstaller not found on disk: ' + UninstallerPath);
    Exit;
  end;

  Log('[ZeManage] Running uninstaller: ' + UninstallerPath +
      ' (PreserveDb=' + IntToStr(Ord(PreserveDb)) + ')');

  if WizardSilent then
    Params := '/VERYSILENT /NORESTART'
  else
    Params := '/SILENT';

  // Custom flag read by CurUninstallStepChanged to skip DB deletion.
  // Only effective if the existing uninstaller was built from this codebase
  // version onwards — older installers will ignore the flag and still wipe the DB.
  if PreserveDb then
    Params := Params + ' /PRESERVEDB=1';

  if Exec(UninstallerPath, Params, '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := (ResultCode = 0);
    Log('[ZeManage] Uninstaller exited with code: ' + IntToStr(ResultCode));
  end
  else
    Log('[ZeManage] Failed to launch uninstaller');
end;

/// Runs all detected existing uninstallers (handles duplicate HKLM + HKCU registration).
/// Pass PreserveDb=True for upgrade scenarios so the local SQLite database is kept;
/// pass False for explicit uninstall (Maintenance page "Uninstall" choice).
function RunExistingUninstaller(PreserveDb: Boolean): Boolean;
var
  Ok1, Ok2: Boolean;
begin
  Ok1 := InvokeUninstaller(ExistingUninstallString, PreserveDb);

  // If a duplicate registration exists in the other hive, uninstall that too.
  if SecondUninstallString <> '' then
  begin
    Log('[ZeManage] Removing duplicate registration in the other hive');
    Ok2 := InvokeUninstaller(SecondUninstallString, PreserveDb);
  end
  else
    Ok2 := True;

  Result := Ok1 and Ok2;
end;


function RedactTokens(const Body: String): String;
var
  Keys: array of String;
  I, P, StartQuote, EndQuote: Integer;
  KeyPattern: String;
begin
  Result := Body;
  SetArrayLength(Keys, 2);
  Keys[0] := '"accessToken"';
  Keys[1] := '"refreshToken"';

  for I := 0 to GetArrayLength(Keys) - 1 do
  begin
    KeyPattern := Keys[I];
    P := Pos(KeyPattern, Result);
    while P > 0 do
    begin
      // Find opening quote of value after colon
      StartQuote := Pos('"', Copy(Result, P + Length(KeyPattern), Length(Result)));
      if StartQuote = 0 then Break;
      StartQuote := P + Length(KeyPattern) + StartQuote - 1;
      // Find closing quote
      EndQuote := Pos('"', Copy(Result, StartQuote + 1, Length(Result)));
      if EndQuote = 0 then Break;
      EndQuote := StartQuote + EndQuote;
      // Replace value with [REDACTED]
      Result := Copy(Result, 1, StartQuote) + '[REDACTED]' + Copy(Result, EndQuote, Length(Result));
      P := Pos(KeyPattern, Copy(Result, StartQuote + 12, Length(Result)));
      if P > 0 then P := P + StartQuote + 11;
    end;
  end;
end;


/// Extracts a string field's value from a flat JSON response body, e.g.
/// ExtractJsonValue('{"success":false,"message":"Device not found"}', 'message')
/// returns 'Device not found'. Returns '' if the key is missing or the value
/// isn't a plain string — callers must supply their own fallback text.
function ExtractJsonValue(const JsonText, Key: String): String;
var
  SearchKey: String;
  ValStart, ValEnd: Integer;
begin
  Result := '';
  SearchKey := '"' + Key + '"';
  ValStart := Pos(SearchKey, JsonText);
  if ValStart = 0 then Exit;

  ValStart := ValStart + Length(SearchKey);
  // Skip the colon and any whitespace between the key and the value.
  while (ValStart <= Length(JsonText)) and
        ((JsonText[ValStart] = ':') or (JsonText[ValStart] = ' ')) do
    ValStart := ValStart + 1;

  if (ValStart > Length(JsonText)) or (JsonText[ValStart] <> '"') then
    Exit; // not a string value (or malformed) — leave Result empty

  ValStart := ValStart + 1; // move past the opening quote
  ValEnd := ValStart;
  while (ValEnd <= Length(JsonText)) and (JsonText[ValEnd] <> '"') do
    ValEnd := ValEnd + 1;

  Result := Copy(JsonText, ValStart, ValEnd - ValStart);
end;


/// Extracts a boolean field's value from a flat JSON response body, e.g.
/// ExtractJsonBool('{"isActive":true,"remoteControl":false}', 'remoteControl') returns False.
/// DefaultVal is returned if the key is missing or malformed, so callers control fail-open
/// vs fail-closed behavior per field.
function ExtractJsonBool(const JsonText, Key: String; DefaultVal: Boolean): Boolean;
var
  SearchKey: String;
  ValStart: Integer;
begin
  Result := DefaultVal;
  SearchKey := '"' + Key + '"';
  ValStart := Pos(SearchKey, JsonText);
  if ValStart = 0 then Exit;

  ValStart := ValStart + Length(SearchKey);
  while (ValStart <= Length(JsonText)) and
        ((JsonText[ValStart] = ':') or (JsonText[ValStart] = ' ')) do
    ValStart := ValStart + 1;

  if Copy(JsonText, ValStart, 4) = 'true' then
    Result := True
  else if Copy(JsonText, ValStart, 5) = 'false' then
    Result := False;
end;


procedure CancelValidationClick(Sender: TObject);
begin
  ValidationCancelled := True;
end;

function GetLicenseErrorMessage(StatusCode: Integer): String;
begin
  case StatusCode of
    400: Result := 'Invalid license key. Please check and try again.';
    401: Result := 'License key is not authorized. Please contact support.';
    403: Result := 'License key has been revoked or suspended.';
    404: Result := 'License key not found. Please verify your key.';
    409: Result := 'This license key is already in use on another device.';
    429: Result := 'Too many attempts. Please wait a moment and try again.';
    500, 502, 503: Result := 'Server is temporarily unavailable. Please try again later.';
  else
    Result := 'Unexpected error (code ' + IntToStr(StatusCode) + '). Please contact support.';
  end;
end;


function RegisterLicense(Token: String): Boolean;
var
  MachineId, SidValue, OsVer: String;
  Http: Variant;
  Body: String;
  CancelBtn: TNewButton;
  Done: Boolean;
  ElapsedSec: Integer;
begin
  Result := False;
  ValidationCancelled := False;
  LastValidationMessage := '';

  MachineId := GetMachineId();
  SidValue  := GetCurrentUserSid();
  OsVer     := GetWmicValue('os get Caption');
  Log('[ZeManage] Machine ID generated: ' + MachineId);
  Log('[ZeManage] SID: ' + SidValue);
  Log('[ZeManage] API URL: {#ApiBaseUrl}/api/v1/tenant/device/auth/register-or-validate-device');

  Body := '{' +
    '"licenseKey":"' + Token + '",' +
    '"machineId":"' + MachineId + '",' +
    '"machineName":"' + ExpandConstant('{computername}') + '",' +
    '"sid":"' + SidValue + '",' +
    '"osVersion":"' + OsVer + '"}';

  if WizardSilent then
  begin
    // Silent mode: synchronous call with tight timeouts, no UI interaction
    Log('[ZeManage] Sending validation request (silent/synchronous)');
    try
      Http := CreateOleObject('WinHttp.WinHttpRequest.5.1');
      Http.SetTimeouts(5000, 5000, 5000, 15000);
      Http.Open('POST', '{#ApiBaseUrl}/api/v1/tenant/device/auth/register-or-validate-device', False);
      Http.SetRequestHeader('Content-Type', 'application/json');
      Http.Send(Body);

      Log('[ZeManage] Validation response: HTTP ' + IntToStr(Http.Status));
      Log('[ZeManage] Response body: ' + RedactTokens(Http.ResponseText));
      if Http.Status = 200 then
      begin
        Result := True;
        AgentInstallEnabled := ExtractJsonBool(Http.ResponseText, 'remoteControl', True);
        Log('[ZeManage] License validation PASSED — remoteControl=' + IntToStr(Ord(AgentInstallEnabled)));
      end
      else
      begin
        LastValidationMessage := GetLicenseErrorMessage(Http.Status);
        Log('[ZeManage] License validation FAILED: HTTP ' + IntToStr(Http.Status));
      end;
    except
      LastValidationMessage := 'Unable to connect to the license server. Please check your network and firewall settings.';
      Log('[ZeManage] License validation error: ' + GetExceptionMessage);
    end;
    Exit;
  end;

  // Interactive mode: async call with cancel button and progress
  LicenseStatus.Caption := 'Validating license...';
  LicenseStatus.Font.Color := $666666;
  LicenseStatus.Visible := True;

  CancelBtn := TNewButton.Create(WizardForm);
  CancelBtn.Parent := LicensePage.Surface;
  CancelBtn.Caption := 'Cancel';
  CancelBtn.Left := 20;
  CancelBtn.Top := LicenseStatus.Top + LicenseStatus.Height + 8;
  CancelBtn.Width := 90;
  CancelBtn.Height := 26;
  CancelBtn.OnClick := @CancelValidationClick;

  WizardForm.NextButton.Enabled := False;
  WizardForm.BackButton.Enabled := False;
  WizardForm.Refresh;

  try
    Http := CreateOleObject('WinHttp.WinHttpRequest.5.1');
    Http.SetTimeouts(5000, 5000, 5000, 35000);
    Http.Open('POST', '{#ApiBaseUrl}/api/v1/tenant/device/auth/register-or-validate-device', True);
    Http.SetRequestHeader('Content-Type', 'application/json');

    Log('[ZeManage] Sending validation request (async)');
    Http.Send(Body);

    ElapsedSec := 0;
    Done := False;
    while (not Done) and (not ValidationCancelled) and (ElapsedSec < 30) do
    begin
      try
        Done := Http.WaitForResponse(1);
      except
        Log('[ZeManage] WaitForResponse exception: ' + GetExceptionMessage);
        Done := False;
        ElapsedSec := 30;
      end;
      ElapsedSec := ElapsedSec + 1;
      if not Done then
      begin
        LicenseStatus.Caption := 'Validating license... (' + IntToStr(ElapsedSec) + 's)';
        WizardForm.Refresh;
      end;
    end;

    if ValidationCancelled then
    begin
      Http.Abort;
      Log('[ZeManage] License validation cancelled by user');
      LicenseStatus.Caption := 'License validation was cancelled.';
      LicenseStatus.Font.Color := $0000CC;
      LastValidationMessage := '';
    end
    else if Done then
    begin
      Log('[ZeManage] Validation response: HTTP ' + IntToStr(Http.Status));
      Log('[ZeManage] Response body: ' + RedactTokens(Http.ResponseText));
      if Http.Status = 200 then
      begin
        Result := True;
        AgentInstallEnabled := ExtractJsonBool(Http.ResponseText, 'remoteControl', True);
        Log('[ZeManage] License validation PASSED — remoteControl=' + IntToStr(Ord(AgentInstallEnabled)));
        LicenseStatus.Visible := False;
      end
      else
      begin
        Log('[ZeManage] License validation FAILED: HTTP ' + IntToStr(Http.Status));
        LicenseStatus.Caption := GetLicenseErrorMessage(Http.Status);
        LicenseStatus.Font.Color := $0000CC;
        LastValidationMessage := LicenseStatus.Caption;
      end;
    end
    else
    begin
      Log('[ZeManage] License validation timed out after 30 seconds');
      LicenseStatus.Caption := 'Could not reach the license server. Please check your internet connection and try again.';
      LicenseStatus.Font.Color := $0000CC;
      LastValidationMessage := LicenseStatus.Caption;
    end;

  except
    Log('[ZeManage] License validation error: ' + GetExceptionMessage);
    LicenseStatus.Caption := 'Unable to connect to the license server. Please check your network and firewall settings.';
    LicenseStatus.Font.Color := $0000CC;
    LastValidationMessage := LicenseStatus.Caption;
  end;

  CancelBtn.Visible := False;
  CancelBtn.Free;
  WizardForm.NextButton.Enabled := True;
  WizardForm.BackButton.Enabled := True;
end;



function IsRevitInstalled(Version: String): Boolean;
begin
  // Check multiple known registry locations and install paths for Revit
  Result :=
    RegKeyExists(HKLM, 'SOFTWARE\Autodesk\Revit\Autodesk Revit ' + Version) or
    RegKeyExists(HKCU, 'SOFTWARE\Autodesk\Revit\Autodesk Revit ' + Version) or
    RegKeyExists(HKLM, 'SOFTWARE\Autodesk\RevitEngine\' + Version) or
    DirExists(ExpandConstant('{autopf}\Autodesk\Revit ' + Version));

  if Result then
    Log('[ZeManage] Detected Revit ' + Version)
  else
    Log('[ZeManage] Revit ' + Version + ' not found');
end;



function IsRevitRunning(): Boolean;
begin
  Result := CheckForMutexes('Global\AutodeskRevit');
end;

/// Populates RunningRevitVersions with a comma-wrapped list of Revit years
/// whose Revit.exe is currently running. Uses PowerShell to enumerate
/// processes and parse the year from the exe path.
procedure PopulateRunningRevitVersions();
var
  ResultCode: Integer;
  TmpFile, ScriptFile, Script: String;
  Lines: TArrayOfString;
  I, P: Integer;
  Line, Year: String;
begin
  RunningRevitVersions := ',';
  TmpFile := ExpandConstant('{tmp}\revit_procs.txt');
  ScriptFile := ExpandConstant('{tmp}\revit_procs.ps1');

  Script :=
    'try {' + #13#10 +
    '  Get-Process -Name Revit -ErrorAction SilentlyContinue |' + #13#10 +
    '    ForEach-Object { $_.Path } |' + #13#10 +
    '    Out-File -FilePath "' + TmpFile + '" -Encoding ASCII' + #13#10 +
    '} catch { }';

  SaveStringToFile(ScriptFile, Script, False);
  Exec('powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptFile + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  DeleteFile(ScriptFile);

  if LoadStringsFromFile(TmpFile, Lines) then
  begin
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      Line := Lines[I];
      // Paths look like "C:\Program Files\Autodesk\Revit 2024\Revit.exe"
      P := Pos('\Revit 20', Line);
      if P > 0 then
      begin
        Year := Copy(Line, P + 7, 4);
        if (Pos(',' + Year + ',', RunningRevitVersions) = 0) then
          RunningRevitVersions := RunningRevitVersions + Year + ',';
      end;
    end;
  end;
  DeleteFile(TmpFile);

  if RunningRevitVersions <> ',' then
    Log('[ZeManage] Running Revit versions detected: ' + RunningRevitVersions)
  else
    Log('[ZeManage] No Revit versions currently running');
end;

function IsRevitVersionRunning(Version: String): Boolean;
begin
  Result := Pos(',' + Version + ',', RunningRevitVersions) > 0;
end;

/// Formats the comma-wrapped RunningRevitVersions (e.g. ",2024,2025,") as a
/// human-friendly string ("Revit 2024, Revit 2025").
function FormatRunningRevitVersions(): String;
var
  S, Year: String;
  P: Integer;
begin
  Result := '';
  S := RunningRevitVersions;
  // Strip leading commas
  while (Length(S) > 0) and (S[1] = ',') do
    Delete(S, 1, 1);
  while Length(S) > 0 do
  begin
    P := Pos(',', S);
    if P > 0 then
    begin
      Year := Copy(S, 1, P - 1);
      Delete(S, 1, P);
    end
    else
    begin
      Year := S;
      S := '';
    end;
    if Year <> '' then
    begin
      if Result <> '' then Result := Result + ', ';
      Result := Result + 'Revit ' + Year;
    end;
  end;
end;



function CheckedAndNotRunning(Cb: TNewCheckBox; Year: String): Boolean;
begin
  Result := Assigned(Cb) and Cb.Checked and (not IsRevitVersionRunning(Year));
  if Assigned(Cb) and Cb.Checked and IsRevitVersionRunning(Year) then
    PartialInstall := True;
end;

// Gates the Agent's [Files]/[Icons]/[Registry] entries — see AgentInstallEnabled above.
function AgentShouldInstall: Boolean; begin Result := AgentInstallEnabled; end;

function InstallR21: Boolean; begin Result := CheckedAndNotRunning(R21, '2021'); end;
function InstallR22: Boolean; begin Result := CheckedAndNotRunning(R22, '2022'); end;
function InstallR23: Boolean; begin Result := CheckedAndNotRunning(R23, '2023'); end;
function InstallR24: Boolean; begin Result := CheckedAndNotRunning(R24, '2024'); end;
function InstallR25: Boolean; begin Result := CheckedAndNotRunning(R25, '2025'); end;
function InstallR26: Boolean; begin Result := CheckedAndNotRunning(R26, '2026'); end;
function InstallR27: Boolean; begin Result := CheckedAndNotRunning(R27, '2027'); end;


function InstallAddonR21: Boolean;
begin
  Result := Assigned(AddonCheck) and AddonCheck.Checked and InstallR21;
end;

function InstallAddonR22: Boolean;
begin
  Result := Assigned(AddonCheck) and AddonCheck.Checked and InstallR22;
end;

function InstallAddonR23: Boolean;
begin
  Result := Assigned(AddonCheck) and AddonCheck.Checked and InstallR23;
end;

function InstallAddonR24: Boolean;
begin
  Result := Assigned(AddonCheck) and AddonCheck.Checked and InstallR24;
end;

function InstallAddonR25: Boolean;
begin
  Result := Assigned(AddonCheck) and AddonCheck.Checked and InstallR25;
end;

function InstallAddonR26: Boolean;
begin
  Result := Assigned(AddonCheck) and AddonCheck.Checked and InstallR26;
end;

function InstallAddonR27: Boolean;
begin
  Result := Assigned(AddonCheck) and AddonCheck.Checked and InstallR27;
end;


procedure PrivacyLinkClick(Sender: TObject);
var
  ErrorCode: Integer;
begin
  ShellExec('open', 'https://zestinetech.github.io/Zestine-Docs/privacy.html#overview', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;


procedure PrivacyCheckClick(Sender: TObject);
begin
  PrivacyAccepted := PrivacyCheck.Checked;
  WizardForm.NextButton.Enabled := PrivacyCheck.Checked;
end;


function AnyVersionSelected(): Boolean;
begin
  Result := (Assigned(R21) and R21.Checked) or
            (Assigned(R22) and R22.Checked) or
            (Assigned(R23) and R23.Checked) or
            (Assigned(R24) and R24.Checked) or
            (Assigned(R25) and R25.Checked) or
            (Assigned(R26) and R26.Checked) or
            (Assigned(R27) and R27.Checked);
end;


procedure StartInstall(Sender: TObject);
begin
  if not LicenseValidated then
  begin
    MsgBox('Please validate your license key before proceeding with the installation.', mbError, MB_OK);
    Exit;
  end;

  if not AnyVersionSelected() then
  begin
    MsgBox('Please select at least one Revit version to continue.', mbError, MB_OK);
    Exit;
  end;

  WizardForm.NextButton.OnClick(nil);
end;


function InitializeSetup(): Boolean;
var
  Choice: Integer;
begin
  Result := True;
  PartialInstall := False;

  // Detect which specific Revit versions are running.
  // Running versions are skipped during install (their files are locked)
  // but other versions install normally. Silent mode returns exit code 3
  // at the end so ManageEngine can flag and retry.
  PopulateRunningRevitVersions();

  // Interactive mode: give the user a Retry loop so they can close Revit and re-check
  // without restarting Setup. Abort cancels install entirely; Ignore proceeds and
  // skips the running versions.
  if not WizardSilent then
  begin
    while RunningRevitVersions <> ',' do
    begin
      Choice := MsgBox(
        'The following Revit versions are currently running:' + #13#10 + #13#10 +
        '    ' + FormatRunningRevitVersions() + #13#10 + #13#10 +
        'These versions cannot be installed/upgraded while Revit is open.' + #13#10 + #13#10 +
        'Retry  - Close Revit, then click Retry to check again.' + #13#10 +
        'Ignore - Continue and SKIP the running versions.' + #13#10 +
        'Abort  - Cancel Setup.',
        mbConfirmation, MB_ABORTRETRYIGNORE);

      if Choice = IDABORT then
      begin
        Log('[ZeManage] User aborted setup (Revit running)');
        Result := False;
        Exit;
      end;

      if Choice = IDIGNORE then
      begin
        Log('[ZeManage] User chose Ignore — proceeding, will skip: ' + RunningRevitVersions);
        Break;
      end;

      // IDRETRY: re-check and loop
      Log('[ZeManage] User chose Retry — re-checking running Revit versions');
      PopulateRunningRevitVersions();
    end;
  end;

  // Detect existing installation for maintenance mode
  IsExistingInstall := IsAlreadyInstalled();

  if WizardSilent then
  begin
    if ExpandConstant('{param:LICENSE|}') = '' then
    begin
      Log('[ZeManage] ERROR: Silent install requires /LICENSE parameter');
      Log('[ZeManage] Usage: ZeManageSetup.exe /VERYSILENT /LICENSE=key /VERSIONS=R25,R26,R27 /ADDON=1');
      Result := False;
      Exit;
    end;

    // Silent installs skip the wizard UI entirely, so NextButtonClick — which normally
    // triggers RegisterLicense when the (unshown) License page would have been submitted —
    // never fires. Register here instead, otherwise register-device is never called and the
    // agent's first validate-device request 401s with "device not registered".
    // NOTE: read the key straight from the command-line param, not LicenseEdit.Text — this
    // runs inside InitializeSetup(), which fires BEFORE InitializeWizard() creates LicenseEdit.
    if not RegisterLicense(Trim(ExpandConstant('{param:LICENSE|}'))) then
    begin
      Log('[ZeManage] ERROR: Silent license registration failed: ' + LastValidationMessage);
      Result := False;
      Exit;
    end;
    LicenseValidated := True;
    Log('[ZeManage] Silent mode: license registered successfully');

    // Auto-upgrade: silently remove old version before installing new one
    if IsExistingInstall then
    begin
      Log('[ZeManage] Silent mode: existing install detected, running silent uninstall first (preserving DB for upgrade)');
      if not RunExistingUninstaller(True) then
        Log('[ZeManage] WARNING: Old uninstaller failed or not found, proceeding anyway');
      Sleep(2000);
    end;
  end;
end;


procedure InitializeWizard();
var
  LicenseLabel: TNewStaticText;
  LicenseTitle: TNewStaticText;
  LicenseDesc: TNewStaticText;
  VersionTitle: TNewStaticText;
  SeparatorPanel: TPanel;
  SilentVersions: String;
begin

  LicenseValidated := False;
  PrivacyAccepted := False;
  AgentInstallEnabled := True;


  // ============================================================
  // WELCOME PAGE - Privacy checkbox + link at bottom
  // ============================================================

  // Shrink WelcomeLabel2 so checkbox is not hidden behind it
  WizardForm.WelcomeLabel2.Height := WizardForm.WelcomeLabel2.Parent.ClientHeight - WizardForm.WelcomeLabel2.Top - 60;

  // Privacy policy checkbox - at bottom of welcome panel
  PrivacyCheck := TNewCheckBox.Create(WizardForm);
  PrivacyCheck.Parent := WizardForm.WelcomeLabel2.Parent;
  PrivacyCheck.Caption := 'I accept the Privacy Policy and Terms of Service';
  PrivacyCheck.Left := WizardForm.WelcomeLabel2.Left;
  PrivacyCheck.Top := WizardForm.WelcomeLabel2.Parent.ClientHeight - 52;
  PrivacyCheck.Width := WizardForm.WelcomeLabel2.Width;
  PrivacyCheck.Height := 20;
  PrivacyCheck.Font.Style := [fsBold];
  PrivacyCheck.Checked := False;
  PrivacyCheck.OnClick := @PrivacyCheckClick;

  // Clickable privacy policy link below checkbox
  PrivacyLink := TNewStaticText.Create(WizardForm);
  PrivacyLink.Parent := WizardForm.WelcomeLabel2.Parent;
  PrivacyLink.Caption := 'Privacy Policy';
  PrivacyLink.Left := WizardForm.WelcomeLabel2.Left;
  PrivacyLink.Top := PrivacyCheck.Top + PrivacyCheck.Height + 2;
  PrivacyLink.Cursor := crHand;
  PrivacyLink.Font.Color := clBlue;
  PrivacyLink.Font.Style := [fsUnderline];
  PrivacyLink.OnClick := @PrivacyLinkClick;


  // ============================================================
  // MAINTENANCE PAGE - shown only when existing install detected
  // ============================================================

  MaintenancePage :=
    CreateCustomPage(
      wpWelcome,
      'ZeManage is Already Installed',
      'Choose what you would like to do'
    );

  // Update / Repair radio button (default)
  MaintenanceUpdateRadio := TNewRadioButton.Create(MaintenancePage);
  MaintenanceUpdateRadio.Parent := MaintenancePage.Surface;
  MaintenanceUpdateRadio.Caption := 'Update / Repair';
  MaintenanceUpdateRadio.Left := 24;
  MaintenanceUpdateRadio.Top := 20;
  MaintenanceUpdateRadio.Width := 380;
  MaintenanceUpdateRadio.Height := 24;
  MaintenanceUpdateRadio.Font.Size := 10;
  MaintenanceUpdateRadio.Font.Style := [fsBold];
  MaintenanceUpdateRadio.Checked := True;

  with TNewStaticText.Create(MaintenancePage) do
  begin
    Parent := MaintenancePage.Surface;
    Caption := 'Reinstall or update ZeManage to the latest version.';
    Left := 44;
    Top := 46;
    Width := 360;
    Font.Size := 9;
    Font.Color := $666666;
  end;

  // Uninstall radio button
  MaintenanceUninstallRadio := TNewRadioButton.Create(MaintenancePage);
  MaintenanceUninstallRadio.Parent := MaintenancePage.Surface;
  MaintenanceUninstallRadio.Caption := 'Uninstall';
  MaintenanceUninstallRadio.Left := 24;
  MaintenanceUninstallRadio.Top := 80;
  MaintenanceUninstallRadio.Width := 380;
  MaintenanceUninstallRadio.Height := 24;
  MaintenanceUninstallRadio.Font.Size := 10;
  MaintenanceUninstallRadio.Font.Style := [fsBold];

  with TNewStaticText.Create(MaintenancePage) do
  begin
    Parent := MaintenancePage.Surface;
    Caption := 'Remove ZeManage completely from this computer.';
    Left := 44;
    Top := 106;
    Width := 360;
    Font.Size := 9;
    Font.Color := $666666;
  end;


  // ============================================================
  // LICENSE ACTIVATION PAGE - Premium styled
  // ============================================================

  LicensePage :=
    CreateCustomPage(
      MaintenancePage.ID,
      'License Activation',
      'Enter your ZeManage license key to activate the plugin'
    );

  LicenseTitle := TNewStaticText.Create(LicensePage);
  LicenseTitle.Parent := LicensePage.Surface;
  LicenseTitle.Caption := 'Activate Your License';
  LicenseTitle.Left := 20;
  LicenseTitle.Top := 12;
  LicenseTitle.Font.Size := 12;
  LicenseTitle.Font.Style := [fsBold];
  LicenseTitle.Font.Color := $3D0206;

  LicenseDesc := TNewStaticText.Create(LicensePage);
  LicenseDesc.Parent := LicensePage.Surface;
  LicenseDesc.Caption := 'Enter the license key provided by your administrator.';
  LicenseDesc.Left := 20;
  LicenseDesc.Top := 38;
  LicenseDesc.Width := 400;
  LicenseDesc.Font.Size := 9;
  LicenseDesc.Font.Color := $666666;

  SeparatorPanel := TPanel.Create(LicensePage);
  SeparatorPanel.Parent := LicensePage.Surface;
  SeparatorPanel.Left := 20;
  SeparatorPanel.Top := 58;
  SeparatorPanel.Width := 400;
  SeparatorPanel.Height := 1;
  SeparatorPanel.Color := $E0E0E0;
  SeparatorPanel.BevelOuter := bvNone;

  LicenseLabel := TNewStaticText.Create(LicensePage);
  LicenseLabel.Parent := LicensePage.Surface;
  LicenseLabel.Caption := 'License Key';
  LicenseLabel.Left := 20;
  LicenseLabel.Top := 72;
  LicenseLabel.Font.Size := 9;
  LicenseLabel.Font.Style := [fsBold];
  LicenseLabel.Font.Color := $333333;

  LicenseEdit := TNewEdit.Create(LicensePage);
  LicenseEdit.Parent := LicensePage.Surface;
  LicenseEdit.Left := 20;
  LicenseEdit.Top := 94;
  LicenseEdit.Width := 400;
  LicenseEdit.Height := 28;
  LicenseEdit.Font.Size := 10;

  LicenseStatus := TNewStaticText.Create(LicensePage);
  LicenseStatus.Parent := LicensePage.Surface;
  LicenseStatus.Left := 20;
  LicenseStatus.Top := 128;
  LicenseStatus.Width := 400;
  LicenseStatus.Font.Color := $666666;
  LicenseStatus.Font.Size := 9;
  LicenseStatus.Visible := False;


  // ============================================================
  // SELECT REVIT VERSIONS PAGE - Premium styled
  // ============================================================

  InstallPage :=
    CreateCustomPage(
      LicensePage.ID,
      'Select Revit Versions',
      'Choose which Revit versions to install ZeManage for'
    );

  VersionTitle := TNewStaticText.Create(InstallPage);
  VersionTitle.Parent := InstallPage.Surface;
  VersionTitle.Caption := 'Choose which Revit version to install';
  VersionTitle.Left := 20;
  VersionTitle.Top := 8;
  VersionTitle.Font.Size := 11;
  VersionTitle.Font.Style := [fsBold];
  VersionTitle.Font.Color := $3D0206;

  R21 := TNewCheckBox.Create(InstallPage);
  R21.Parent := InstallPage.Surface;
  R21.Caption := '  Revit 2021';
  R21.Left := 28;
  R21.Top := 34;
  R21.Width := 360;
  R21.Height := 22;
  R21.Font.Size := 9;

  R22 := TNewCheckBox.Create(InstallPage);
  R22.Parent := InstallPage.Surface;
  R22.Caption := '  Revit 2022';
  R22.Left := 28;
  R22.Top := 56;
  R22.Width := 360;
  R22.Height := 22;
  R22.Font.Size := 9;

  R23 := TNewCheckBox.Create(InstallPage);
  R23.Parent := InstallPage.Surface;
  R23.Caption := '  Revit 2023';
  R23.Left := 28;
  R23.Top := 78;
  R23.Width := 360;
  R23.Height := 22;
  R23.Font.Size := 9;

  R24 := TNewCheckBox.Create(InstallPage);
  R24.Parent := InstallPage.Surface;
  R24.Caption := '  Revit 2024';
  R24.Left := 28;
  R24.Top := 100;
  R24.Width := 360;
  R24.Height := 22;
  R24.Font.Size := 9;

  R25 := TNewCheckBox.Create(InstallPage);
  R25.Parent := InstallPage.Surface;
  R25.Caption := '  Revit 2025';
  R25.Left := 28;
  R25.Top := 122;
  R25.Width := 360;
  R25.Height := 22;
  R25.Font.Size := 9;

  R26 := TNewCheckBox.Create(InstallPage);
  R26.Parent := InstallPage.Surface;
  R26.Caption := '  Revit 2026';
  R26.Left := 28;
  R26.Top := 144;
  R26.Width := 360;
  R26.Height := 22;
  R26.Font.Size := 9;

  R27 := TNewCheckBox.Create(InstallPage);
  R27.Parent := InstallPage.Surface;
  R27.Caption := '  Revit 2027';
  R27.Left := 28;
  R27.Top := 166;
  R27.Width := 360;
  R27.Height := 22;
  R27.Font.Size := 9;

  SeparatorPanel := TPanel.Create(InstallPage);
  SeparatorPanel.Parent := InstallPage.Surface;
  SeparatorPanel.Left := 20;
  SeparatorPanel.Top := 196;
  SeparatorPanel.Width := 400;
  SeparatorPanel.Height := 1;
  SeparatorPanel.Color := $E0E0E0;
  SeparatorPanel.BevelOuter := bvNone;

  AddonCheck := TNewCheckBox.Create(InstallPage);
  AddonCheck.Parent := InstallPage.Surface;
  AddonCheck.Caption := '  ZeConnect (Addons)';
  AddonCheck.Left := 28;
  AddonCheck.Top := 206;
  AddonCheck.Width := 360;
  AddonCheck.Height := 22;
  AddonCheck.Font.Size := 9;
  AddonCheck.Checked := True;


  InstallButton := TNewButton.Create(WizardForm);
  InstallButton.Parent := WizardForm;
  InstallButton.Caption := 'Install';
  InstallButton.Left := WizardForm.NextButton.Left;
  InstallButton.Top := WizardForm.NextButton.Top;
  InstallButton.Width := WizardForm.NextButton.Width;
  InstallButton.Height := WizardForm.NextButton.Height;
  InstallButton.Visible := False;
  InstallButton.OnClick := @StartInstall;



  // Hide the small header logo for a cleaner premium look
  WizardForm.WizardSmallBitmapImage.Visible := False;

  if IsRevitInstalled('2021') then R21.Checked := True;
  if IsRevitInstalled('2022') then R22.Checked := True;
  if IsRevitInstalled('2023') then R23.Checked := True;
  if IsRevitInstalled('2024') then R24.Checked := True;
  if IsRevitInstalled('2025') then R25.Checked := True;
  if IsRevitInstalled('2026') then R26.Checked := True;
  if IsRevitInstalled('2027') then R27.Checked := True;

  // Silent mode: pre-fill controls from command-line params
  if WizardSilent then
  begin
    PrivacyAccepted := True;
    LicenseEdit.Text := Trim(ExpandConstant('{param:LICENSE|}'));

    // /VERSIONS overrides auto-detection if provided
    SilentVersions := ExpandConstant('{param:VERSIONS|}');
    if SilentVersions <> '' then
    begin
      R21.Checked := Pos('R21', SilentVersions) > 0;
      R22.Checked := Pos('R22', SilentVersions) > 0;
      R23.Checked := Pos('R23', SilentVersions) > 0;
      R24.Checked := Pos('R24', SilentVersions) > 0;
      R25.Checked := Pos('R25', SilentVersions) > 0;
      R26.Checked := Pos('R26', SilentVersions) > 0;
      R27.Checked := Pos('R27', SilentVersions) > 0;
    end;
    // else: keep auto-detected Revit versions

    AddonCheck.Checked := ExpandConstant('{param:ADDON|1}') = '1';

    Log('[ZeManage] Silent mode: LICENSE=' + LicenseEdit.Text);
    Log('[ZeManage] Silent mode: VERSIONS=' + SilentVersions);
    Log('[ZeManage] Silent mode: ADDON=' + ExpandConstant('{param:ADDON|1}'));
  end;

end;


function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;

  // Existing install: skip Welcome, show Maintenance page first
  if (PageID = wpWelcome) and IsExistingInstall and (not WizardSilent) then
  begin
    Result := True;
    Log('[ZeManage] Skipping Welcome page - existing install detected');
  end;

  // Fresh install: skip Maintenance page
  if (PageID = MaintenancePage.ID) and (not IsExistingInstall) then
  begin
    Result := True;
    Log('[ZeManage] Skipping Maintenance page - fresh install');
  end;

  // Silent mode: always skip Maintenance page
  if (PageID = MaintenancePage.ID) and WizardSilent then
  begin
    Result := True;
    Log('[ZeManage] Skipping Maintenance page - silent mode');
  end;
end;


procedure CurPageChanged(CurPageID: Integer);
begin
  // Maintenance page: ensure Next is enabled, hide Back
  if CurPageID = MaintenancePage.ID then
  begin
    WizardForm.NextButton.Enabled := True;
    WizardForm.NextButton.Visible := True;
    WizardForm.BackButton.Visible := False;
  end;

  // Welcome page: disable Next until privacy is accepted
  if CurPageID = wpWelcome then
  begin
    WizardForm.NextButton.Enabled := PrivacyAccepted;
    WizardForm.BackButton.Visible := not IsExistingInstall;
    PrivacyCheck.Visible := True;
    PrivacyLink.Visible := True;
  end
  else
  begin
    PrivacyCheck.Visible := False;
    PrivacyLink.Visible := False;
  end;

  // Install page: show custom Install button, hide Next/Back
  // Skip button swap in silent mode — Inno Setup needs NextButton to auto-advance
  if (CurPageID = InstallPage.ID) and (not WizardSilent) then
  begin
    WizardForm.NextButton.Visible := False;
    WizardForm.BackButton.Visible := False;
    InstallButton.Visible := True;
  end
  else
  begin
    InstallButton.Visible := False;
  end;
end;



procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  // Skip "are you sure?" prompt after uninstall completes
  if UninstallCompleted then
    Confirm := False;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  // Maintenance page: handle Update/Repair vs Uninstall
  if CurPageID = MaintenancePage.ID then
  begin
    if MaintenanceUninstallRadio.Checked then
    begin
      Log('[ZeManage] User chose Uninstall from maintenance page');
      if MsgBox('This will completely remove ZeManage from your computer. Do you want to continue?', mbConfirmation, MB_YESNO) = IDYES then
      begin
        // User explicitly chose Uninstall — wipe the DB too
        RunExistingUninstaller(False);
        UninstallCompleted := True;
        WizardForm.Close;
        Result := False;
        Exit;
      end
      else
      begin
        Result := False;
        Exit;
      end;
    end;
    Log('[ZeManage] User chose Update/Repair from maintenance page');
  end;

  // Welcome page: require privacy policy acceptance
  if CurPageID = wpWelcome then
  begin
    if not PrivacyAccepted then
    begin
      MsgBox('Please review and accept the Privacy Policy and Terms of Service to continue.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;

  Log('[ZeManage] NextButtonClick: CurPageID=' + IntToStr(CurPageID) + ' LicensePageID=' + IntToStr(LicensePage.ID));

  if CurPageID = LicensePage.ID then
  begin
    LicenseEdit.Text := Trim(LicenseEdit.Text);

    if LicenseEdit.Text = '' then
    begin
      MsgBox('Please enter your license key to continue.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if not RegisterLicense(LicenseEdit.Text) then
    begin
      if LastValidationMessage <> '' then
        MsgBox(LastValidationMessage, mbError, MB_OK);
      Result := False;
      Exit;
    end;

    LicenseValidated := True;
    Log('[ZeManage] License validated successfully');
  end;

  if CurPageID = InstallPage.ID then
  begin
    if not LicenseValidated then
    begin
      MsgBox('Please validate your license key before proceeding with the installation.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if not AnyVersionSelected() then
    begin
      MsgBox('Please select at least one Revit version to continue.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;


procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  AgentExe: String;
begin
  if CurStep = ssInstall then
  begin
    // Marker the watchdog checks before relaunching the Agent — without this, killing the Agent
    // below (to overwrite its files) could get raced by the watchdog restarting it mid-copy.
    // Deleted again in ssPostInstall once the new files are fully in place.
    ForceDirectories(ExpandConstant('{app}\Agent'));
    SaveStringToFile(ExpandConstant('{app}\Agent\.installing'), '1', False);

    // Ask both processes to exit themselves first — this is the path that actually works when
    // this installer isn't elevated (per-user install). Give them a couple seconds to actually
    // shut down before falling back to taskkill, which only succeeds when this installer IS
    // elevated (ProcessProtection denies PROCESS_TERMINATE to non-admins on both processes).
    SignalGracefulExit('ZeManageAgentWatchdog-RequestExit');
    SignalGracefulExit('ZeManageAgent-RequestExit');
    Sleep(2000);

    // Stop Watchdog first — otherwise it just relaunches the Agent a few seconds after the next line
    Exec('taskkill.exe', '/F /IM ZeManage.Agent.Watchdog.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Log('[ZeManage] Watchdog process stopped (exit code: ' + IntToStr(ResultCode) + ')');

    // Stop Agent process before install so Agent files can be overwritten
    Exec('taskkill.exe', '/F /IM ZeManage.Agent.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Log('[ZeManage] Agent process stopped (exit code: ' + IntToStr(ResultCode) + ')');

    // Remove addin files from the OTHER location to prevent duplicate loading.
    // If user previously installed as admin (ProgramData) and now installs as user (AppData),
    // the old ProgramData copy would cause Revit to load the plugin twice.
    Log('[ZeManage] Cleaning duplicate addins from opposite install location');
    CleanDuplicateAddins();

    // Remove stale auth tokens — the installer re-registers the device with a fresh license key,
    // so old refresh tokens are invalid. Prevents stale token conflicts on first launch.
    if ExpandConstant('{username}') <> 'SYSTEM' then
    begin
      if DeleteFile(ExpandConstant('{userappdata}\BIManage\auth.dat')) then
        Log('[ZeManage] Deleted stale auth tokens before install')
      else
        Log('[ZeManage] No existing auth tokens found');
    end;
  end;

  if CurStep = ssPostInstall then
  begin
    // New files are fully in place now — safe for the watchdog to relaunch the Agent again
    DeleteFile(ExpandConstant('{app}\Agent\.installing'));

    // Agent-side setup (config + first launch) — skipped entirely for a remoteControl=False
    // (Revit-only) install: no appsettings.json, no local capture, nothing to launch.
    if AgentInstallEnabled then
    begin
      // Write appsettings.json for Agent with the backend URL
      // Silent install: use /BACKENDURL param, default to localhost
      // Interactive install: use localhost (user can change later in config)
      SaveStringToFile(
        ExpandConstant('{app}\Agent\appsettings.json'),
        '{' + #13#10 +
        '  "Agent": {' + #13#10 +
        '    "BackendBaseUrl": "' + ExpandConstant('{param:BACKENDURL|http://10.10.40.75:5000}') + '",' + #13#10 +
        '    "AllowInsecureSsl": true,' + #13#10 +
        '    "LicenseKey": "' + LicenseEdit.Text + '"' + #13#10 +
        '  }' + #13#10 +
        '}',
        False);
      Log('[ZeManage] Agent appsettings.json written with BackendBaseUrl=' +
          ExpandConstant('{param:BACKENDURL|http://10.10.40.75:5000}'));

      // Launch Agent via Task Scheduler as the current (non-elevated) user.
      // /IT = interactive session required (runs as real logged-in user, not elevated admin).
      // /RL LIMITED = non-elevated token. Delete task immediately after firing — it already ran.
      AgentExe := ExpandConstant('{app}\Agent\ZeManage.Agent.exe');
      if FileExists(AgentExe) then
      begin
        Exec(ExpandConstant('{sys}\schtasks.exe'),
             '/Create /F /SC ONCE /ST 00:00 /TN "ZeManageAgentStart" ' +
             '/TR "\"' + AgentExe + '\" --minimized" /IT /RL LIMITED',
             '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        Log('[ZeManage] Agent task created (code ' + IntToStr(ResultCode) + ')');
        Exec(ExpandConstant('{sys}\schtasks.exe'),
             '/Run /TN "ZeManageAgentStart"',
             '', SW_HIDE, ewNoWait, ResultCode);
        Log('[ZeManage] Agent task fired');
        Exec(ExpandConstant('{sys}\schtasks.exe'),
             '/Delete /F /TN "ZeManageAgentStart"',
             '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        Log('[ZeManage] Agent task cleaned up');
      end
      else
        Log('[ZeManage] WARNING: Agent EXE not found at: ' + AgentExe);
    end
    else
      Log('[ZeManage] remoteControl=False — Revit-only install, Agent setup skipped');
  end;

  if CurStep = ssDone then
  begin
    // Partial install: log clearly and return exit code 3 so ManageEngine
    // can flag the deployment as incomplete and retry later.
    if PartialInstall then
    begin
      Log('[ZeManage] ============================================');
      Log('[ZeManage] INCOMPLETE INSTALL');
      Log('[ZeManage] Skipped Revit versions (running): ' + RunningRevitVersions);
      Log('[ZeManage] Exiting with code 3');
      Log('[ZeManage] ============================================');
      if WizardSilent then
        ExitProcess(3);
    end;
  end;
end;


function InitializeUninstall(): Boolean;
var
  PreserveDb: Boolean;
  MachineId, ApiMessage: String;
  Http: Variant;
  RequestOk: Boolean;
begin
  Result := True;

  // Ask the Agent/Watchdog to exit themselves before [UninstallRun]'s declarative taskkill
  // entries run later — the primary path when this uninstaller isn't elevated (per-user
  // install); taskkill alone only actually succeeds when elevated, per ProcessProtection's DACL.
  SignalGracefulExit('ZeManageAgentWatchdog-RequestExit');
  SignalGracefulExit('ZeManageAgent-RequestExit');
  Sleep(2000);

  // Block uninstall if Revit is running — addin DLLs are locked by Revit
  // and cannot be deleted; partial uninstall would leave a broken state.
  if IsRevitRunning() then
  begin
    if UninstallSilent then
    begin
      Log('[ZeManage] ERROR: Autodesk Revit is running, cannot uninstall');
      Result := False;
      Exit;
    end;
    if MsgBox(
        'Autodesk Revit is currently running.' + #13#10 + #13#10 +
        'Please close all Revit windows and click OK to continue, or Cancel to abort.',
        mbConfirmation, MB_OKCANCEL) <> IDOK then
    begin
      Result := False;
      Exit;
    end;
    // Re-check after user clicks OK
    if IsRevitRunning() then
    begin
      MsgBox('Autodesk Revit is still running. Please close it and run Uninstall again.',
        mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;

  // /PRESERVEDB=1 is set by the new installer's RunExistingUninstaller(True) when this
  // uninstaller is invoked as part of an UPGRADE (not a real uninstall) — skip the server
  // check entirely in that case, so a machine with no network access can still upgrade
  // in place. (CurUninstallStepChanged still checks the same flag to skip DB/token deletion.)
  PreserveDb := ExpandConstant('{param:PRESERVEDB|0}') = '1';
  if PreserveDb then
  begin
    Log('[ZeManage] PRESERVEDB=1 detected — skipping server uninstall check (upgrade mode)');
    Exit;
  end;

  // Confirm the uninstall with the server BEFORE removing anything — deliberately not
  // best-effort. The device must only be uninstalled once the server confirms success;
  // this is intentional (an employee shouldn't be able to remove the tracking agent by
  // disconnecting from the network), not a bug. Any failure — an error response from
  // the API, or the server being unreachable — shows the exact reason and aborts the
  // uninstall with nothing deleted.
  Log('[ZeManage] Uninstall requested - confirming with server before proceeding');
  RequestOk := False;
  ApiMessage := '';
  try
    MachineId := GetMachineId();
    Http := CreateOleObject('WinHttp.WinHttpRequest.5.1');
    Http.SetTimeouts(5000, 5000, 5000, 10000);
    Http.Open('POST', '{#UninstallEndpoint}', False);
    Http.SetRequestHeader('Content-Type', 'application/json');
    Http.Send('{' +
      '"machineId":"' + MachineId + '",' +
      '"computerName":"' + ExpandConstant('{computername}') + '",' +
      '"computerUserName":"' + ExpandConstant('{username}') + '"}');

    Log('[ZeManage] Uninstall confirmation response: HTTP ' + IntToStr(Http.Status));
    Log('[ZeManage] Response body: ' + Http.ResponseText);

    if (Http.Status >= 200) and (Http.Status < 300) then
      RequestOk := True
    else
    begin
      ApiMessage := ExtractJsonValue(Http.ResponseText, 'message');
      if ApiMessage = '' then
        ApiMessage := 'Server rejected the uninstall request (HTTP ' + IntToStr(Http.Status) + ').';
    end;
  except
    ApiMessage := 'Could not reach the ZeManage server. Please check your network connection and try again.';
    Log('[ZeManage] Uninstall confirmation failed: ' + GetExceptionMessage);
  end;

  if not RequestOk then
  begin
    Log('[ZeManage] Uninstall BLOCKED - server did not confirm success: ' + ApiMessage);
    if not UninstallSilent then
      MsgBox('Unable to uninstall ZeManage:' + #13#10 + #13#10 + ApiMessage, mbError, MB_OK);
    Result := False;
    Exit;
  end;

  Log('[ZeManage] Server confirmed uninstall - proceeding');
end;


procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Years: array of String;
  I: Integer;
  PreserveDb: Boolean;
begin
  if CurUninstallStep = usUninstall then
  begin
    // /PRESERVEDB=1 is set by the new installer's RunExistingUninstaller(True)
    // when this uninstaller is invoked as part of an UPGRADE (not a real uninstall).
    // In that case skip DB + auth-token deletion so the user's data survives the upgrade.
    // Server uninstall confirmation now happens earlier, in InitializeUninstall — this
    // step is only reached at all once that check has already passed (or was skipped
    // here for PRESERVEDB upgrades), so there is nothing to notify at this point.
    PreserveDb := ExpandConstant('{param:PRESERVEDB|0}') = '1';
    if PreserveDb then
      Log('[ZeManage] PRESERVEDB=1 detected — skipping DB and auth token deletion (upgrade mode)');

    // Clean addin files and database.
    // When running as SYSTEM (ManageEngine), enumerate all user profiles;
    // otherwise clean current user's paths only.
    try
      if ExpandConstant('{username}') = 'SYSTEM' then
      begin
        Log('[ZeManage] Running as SYSTEM - cleaning all user profiles');
        CleanAllUserProfiles(PreserveDb);
      end
      else
      begin
        Log('[ZeManage] Cleaning addin files from both install locations');
        SetArrayLength(Years, 7);
        Years[0] := '2021'; Years[1] := '2022'; Years[2] := '2023';
        Years[3] := '2024'; Years[4] := '2025'; Years[5] := '2026';
        Years[6] := '2027';

        for I := 0 to 6 do
        begin
          try
            CleanAddinFromBothLocations(Years[I]);
          except
            Log('[ZeManage] Failed to clean addins for ' + Years[I] + ': ' + GetExceptionMessage);
          end;
        end;

        // Remove local SQLite database (leave logs intact) — skip on upgrade
        if not PreserveDb then
        begin
          try
            if DeleteFile(ExpandConstant('{localappdata}\BIManageRevit\Logs\bimanage.db')) then
              Log('[ZeManage] Deleted local database: bimanage.db')
            else
              Log('[ZeManage] No local database found or could not delete');
          except
            Log('[ZeManage] Failed to delete database: ' + GetExceptionMessage);
          end;
        end;

        // Remove auth tokens (DPAPI-protected refresh token) — skip on upgrade
        if not PreserveDb then
        try
          if ExpandConstant('{username}') <> 'SYSTEM' then
          begin
            if DeleteFile(ExpandConstant('{userappdata}\BIManage\auth.dat')) then
              Log('[ZeManage] Deleted auth tokens: auth.dat')
            else
              Log('[ZeManage] No auth tokens found or could not delete');
          end;
        except
          Log('[ZeManage] Failed to delete auth tokens: ' + GetExceptionMessage);
        end;
      end;
    except
      Log('[ZeManage] File cleanup failed: ' + GetExceptionMessage);
    end;

    // Force-remove uninstall registry key from both hives
    // (fixes ghost Control Panel entry when running as SYSTEM via ManageEngine)
    try
      RegDeleteKeyIncludingSubkeys(HKLM,
        'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{C1E4C6A3-8E6B-4F4B-8D7A-ZEMANAGE001}}_is1');
      RegDeleteKeyIncludingSubkeys(HKCU,
        'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{C1E4C6A3-8E6B-4F4B-8D7A-ZEMANAGE001}}_is1');
      Log('[ZeManage] Registry cleanup complete');
    except
      Log('[ZeManage] Registry cleanup failed: ' + GetExceptionMessage);
    end;

    Log('[ZeManage] Uninstall cleanup complete');
  end;
end;
