; ============================================================================
; WinPDF — Official Installer Script (Inno Setup 7)
; ============================================================================

#ifndef AppVersion
  #define AppVersion "0.5.4-alpha.5"
#endif

#ifndef AppArch
  #define AppArch "x64"
#endif

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\WinPDF-" + AppVersion + "-win-" + AppArch
#endif

#define MyAppName "WinPDF"
#define MyAppPublisher "Fluentify"
#define MyAppURL "https://github.com/pedrofleck/winpdf"
#define MyAppExeName "WinPDF.exe"
#define MyAppAssocName "PDF Document (WinPDF)"
#define MyAppAssocExt ".pdf"
#define MyAppAssocKey "Fluentify.WinPDF.pdf"

[Setup]
; IMPORTANT: This fixed AppId ensures future versions detect existing installations
; and upgrade in-place without duplicating entries or losing preferences.
AppId={{8F4E9A2B-5C7D-4E1A-9B3F-2D6C8A1E5F09}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases

DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no

; Installs directly for All Users (Admin), without initial prompt
PrivilegesRequired=admin

#if AppArch == "ARM64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

OutputDir=..\artifacts\installer
OutputBaseFilename=WinPDF-Setup-{#AppVersion}-win-{#AppArch}
SetupIconFile=..\src\WinPDF\Assets\appicon.ico

; Modern visual styling with automatic Light and Dark Mode support (Inno Setup 7):
WizardStyle=modern dynamic
WizardImageStretch=yes
WizardImageFile=..\src\WinPDF\Assets\wizard-large-light.bmp
WizardImageFileDynamicDark=..\src\WinPDF\Assets\wizard-large-dark.bmp
WizardSmallImageFile=..\src\WinPDF\Assets\wizard-small-light.bmp
WizardSmallImageFileDynamicDark=..\src\WinPDF\Assets\wizard-small-dark.bmp

UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

; Automatic detection and upgrade over previous versions:
UsePreviousAppDir=yes
UsePreviousTasks=yes
CloseApplications=yes
RestartApplications=no

Compression=lzma2/ultra64
SolidCompression=yes
ChangesAssociations=yes
ShowLanguageDialog=auto

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[CustomMessages]
english.CreateStartMenuIcon=Create a &Start Menu shortcut
brazilianportuguese.CreateStartMenuIcon=Criar um atalho no &Menu Iniciar
english.RemoveSettingsQuestion=Do you also want to remove your saved {#MyAppName} configuration files and preferences?
brazilianportuguese.RemoveSettingsQuestion=Deseja remover também seus arquivos de configuração e preferências salvas do {#MyAppName}?
english.ReinstallQuestion=WinPDF version %1 is already installed on your computer.%n%nDo you want to reinstall it?
brazilianportuguese.ReinstallQuestion=A versão %1 do WinPDF já está instalada neste computador.%n%nDeseja reinstalar?
english.UpgradeQuestion=An earlier version of WinPDF (%1) is installed.%n%nDo you want to update it to version %2?
brazilianportuguese.UpgradeQuestion=Uma versão anterior do WinPDF (%1) está instalada.%n%nDeseja atualizar para a versão %2?
english.DowngradeQuestion=A newer version of WinPDF (%1) is already installed.%n%nDo you want to replace it with version %2?
brazilianportuguese.DowngradeQuestion=Uma versão mais recente do WinPDF (%1) já está instalada.%n%nDeseja substituí-la pela versão %2?

[Tasks]
Name: "startmenuicon"; Description: "{cm:CreateStartMenuIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Start Menu shortcut (optional via checkbox)
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startmenuicon
; Desktop shortcut (optional via checkbox)
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; 1. ProgId for .pdf file association in Windows 10/11
Root: HKLM; Subkey: "Software\Classes\{#MyAppAssocKey}"; ValueType: string; ValueName: ""; ValueData: "{#MyAppAssocName}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\{#MyAppAssocKey}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Assets\pdf-document.ico,0"
Root: HKLM; Subkey: "Software\Classes\{#MyAppAssocKey}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

; 2. Register in OpenWithProgids to appear in "Open with" and be selectable as default PDF reader
Root: HKLM; Subkey: "Software\Classes\{#MyAppAssocExt}\OpenWithProgids"; ValueType: string; ValueName: "{#MyAppAssocKey}"; ValueData: ""; Flags: uninsdeletevalue

; 3. Register application in Windows Default Apps (RegisteredApplications / Capabilities)
Root: HKLM; Subkey: "Software\Fluentify\WinPDF\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Fluentify\WinPDF\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Fast and modern native PDF reader for Windows 11"
Root: HKLM; Subkey: "Software\Fluentify\WinPDF\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "{#MyAppAssocKey}"
Root: HKLM; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "WinPDF"; ValueData: "Software\Fluentify\WinPDF\Capabilities"; Flags: uninsdeletevalue

; 4. Register in App Paths
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// State tracking to avoid prompting repeatedly if the user navigates Back and Next
var
  VersionPromptAccepted: Boolean;

// Extracts the next integer token from a version string S, advancing past dots and non-digits.
function ParseVersionNumber(var S: string): Integer;
var
  NumStr: string;
begin
  NumStr := '';
  while (Length(S) > 0) and not ((S[1] >= '0') and (S[1] <= '9')) do
    Delete(S, 1, 1);

  while (Length(S) > 0) and (S[1] >= '0') and (S[1] <= '9') do
  begin
    NumStr := NumStr + S[1];
    Delete(S, 1, 1);
  end;

  if (Length(S) > 0) and (S[1] = '.') then
    Delete(S, 1, 1);

  if NumStr <> '' then
    Result := StrToIntDef(NumStr, 0)
  else
    Result := 0;
end;

// Compares two semantic/dot version strings (e.g. "0.4.2-alpha.4" vs "0.5.2-alpha.5").
// Returns < 0 if V1 < V2, 0 if V1 = V2, > 0 if V1 > V2.
function CompareVersionStrings(V1, V2: string): Integer;
var
  S1, S2: string;
  N1, N2: Integer;
  Part: Integer;
begin
  if SameText(Trim(V1), Trim(V2)) then
  begin
    Result := 0;
    Exit;
  end;

  S1 := Trim(V1);
  S2 := Trim(V2);

  for Part := 1 to 4 do
  begin
    N1 := ParseVersionNumber(S1);
    N2 := ParseVersionNumber(S2);
    if N1 > N2 then
    begin
      Result := 1;
      Exit;
    end
    else if N1 < N2 then
    begin
      Result := -1;
      Exit;
    end;
  end;

  Result := CompareText(S1, S2);
end;

// Retrieves the installed version from Windows Uninstall registry keys or existing executable.
function GetInstalledVersion(var InstalledVer: string): Boolean;
var
  UninstallKey: string;
  InstallLoc: string;
  ExePath: string;
begin
  Result := False;
  InstalledVer := '';
  InstallLoc := '';
  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F4E9A2B-5C7D-4E1A-9B3F-2D6C8A1E5F09}_is1';

  // 1. Try 64-bit HKLM
  if IsWin64 then
  begin
    if RegQueryStringValue(HKLM64, UninstallKey, 'DisplayVersion', InstalledVer) and (Trim(InstalledVer) <> '') then
    begin
      Result := True;
      Exit;
    end;
    if RegQueryStringValue(HKLM32, UninstallKey, 'DisplayVersion', InstalledVer) and (Trim(InstalledVer) <> '') then
    begin
      Result := True;
      Exit;
    end;
  end;

  // 2. Try standard HKLM
  if RegQueryStringValue(HKLM, UninstallKey, 'DisplayVersion', InstalledVer) and (Trim(InstalledVer) <> '') then
  begin
    Result := True;
    Exit;
  end;

  // 3. Try HKCU (in case installed or tested per-user)
  if IsWin64 then
  begin
    if RegQueryStringValue(HKCU64, UninstallKey, 'DisplayVersion', InstalledVer) and (Trim(InstalledVer) <> '') then
    begin
      Result := True;
      Exit;
    end;
    if RegQueryStringValue(HKCU32, UninstallKey, 'DisplayVersion', InstalledVer) and (Trim(InstalledVer) <> '') then
    begin
      Result := True;
      Exit;
    end;
  end;

  if RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', InstalledVer) and (Trim(InstalledVer) <> '') then
  begin
    Result := True;
    Exit;
  end;

  // 4. If registry key exists with InstallLocation but DisplayVersion was empty, check the exe
  if (IsWin64 and RegQueryStringValue(HKLM64, UninstallKey, 'InstallLocation', InstallLoc)) or
     RegQueryStringValue(HKLM, UninstallKey, 'InstallLocation', InstallLoc) or
     (IsWin64 and RegQueryStringValue(HKCU64, UninstallKey, 'InstallLocation', InstallLoc)) or
     RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', InstallLoc) then
  begin
    if Trim(InstallLoc) <> '' then
    begin
      ExePath := AddBackslash(Trim(InstallLoc)) + '{#MyAppExeName}';
      if FileExists(ExePath) then
      begin
        if GetVersionNumbersString(ExePath, InstalledVer) and (Trim(InstalledVer) <> '') then
        begin
          Result := True;
          Exit;
        end;
      end;
    end;
  end;
end;

// Validates whether to proceed when advancing from the Welcome page.
function NextButtonClick(CurPageID: Integer): Boolean;
var
  InstalledVer: string;
  Cmp: Integer;
  PromptMsg: string;
begin
  Result := True;

  if (CurPageID = wpWelcome) and (not VersionPromptAccepted) and (not WizardSilent) then
  begin
    if GetInstalledVersion(InstalledVer) then
    begin
      Cmp := CompareVersionStrings(InstalledVer, '{#AppVersion}');
      if Cmp = 0 then
      begin
        PromptMsg := FmtMessage(ExpandConstant('{cm:ReinstallQuestion}'), [InstalledVer]);
        if MsgBox(PromptMsg, mbConfirmation, MB_YESNO or MB_DEFBUTTON1) <> IDYES then
        begin
          Result := False;
          Exit;
        end;
      end
      else if Cmp < 0 then
      begin
        PromptMsg := FmtMessage(ExpandConstant('{cm:UpgradeQuestion}'), [InstalledVer, '{#AppVersion}']);
        if MsgBox(PromptMsg, mbConfirmation, MB_YESNO or MB_DEFBUTTON1) <> IDYES then
        begin
          Result := False;
          Exit;
        end;
      end
      else
      begin
        PromptMsg := FmtMessage(ExpandConstant('{cm:DowngradeQuestion}'), [InstalledVer, '{#AppVersion}']);
        if MsgBox(PromptMsg, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then
        begin
          Result := False;
          Exit;
        end;
      end;

      VersionPromptAccepted := True;
    end;
  end;
end;

// During interactive uninstall, ask if user wants to remove %LOCALAPPDATA%\WinPDF
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  SettingsPath: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    SettingsPath := ExpandConstant('{localappdata}\WinPDF');
    if DirExists(SettingsPath) then
    begin
      if not UninstallSilent then
      begin
        if MsgBox(ExpandConstant('{cm:RemoveSettingsQuestion}'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        begin
          DelTree(SettingsPath, True, True, True);
        end;
      end;
    end;
  end;
end;