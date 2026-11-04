#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\dist\app\win-x64"
#endif

; Display/brand name shown to users. The app's internal identity stays "Gauge": the
; exe, install folder, AppId, Run-key value and data paths are all unchanged, so an
; existing Gauge install upgrades in place under the new brand.
#define MyAppName "AgentGauge"
#define MyInstallName "Gauge"
#define MyAppExeName "Gauge.exe"

[Setup]
AppId={{C7092916-3DCD-4A16-AC81-4A9054B4C74C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=AgentGauge
DefaultDirName={localappdata}\Programs\{#MyInstallName}
DefaultGroupName={#MyInstallName}
UninstallDisplayIcon={app}\{#MyAppExeName}
; Without this the Apps & Features name is "AgentGauge version 0.7.5", translated into the
; wizard's language, so it could never equal the package name winget and others match on.
UninstallDisplayName={#MyAppName}
; Outward-facing brand icon (the GaugeSetup.exe icon + wizard title bar). Matches the
; icon embedded in Gauge.exe, which is what UninstallDisplayIcon resolves to above.
SetupIconFile=..\Assets\gauge_appicon.ico
OutputDir=..\dist
OutputBaseFilename=GaugeSetup-win-x64
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=no
; Auto-pick the wizard language from the system UI language, with no Select Language
; dialog. An unmatched system language falls back to the first [Languages] entry
; (English) — mirroring the app's own ko/ja/→en resolution.
ShowLanguageDialog=no
LanguageDetectionMethod=uilanguage
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousGroup=yes

; English is listed first so it is the default fallback for any system language that
; is neither Korean nor Japanese. All three .isl files ship with Inno Setup 6.
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
; Interactive install: optional launch checkbox on the finished page.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; Silent update (in-app updater runs Setup with /VERYSILENT): there is no finished
; page, so relaunch Gauge automatically once the files are in place. The --updated
; flag tells the app this launch followed an update so it opens its window once,
; confirming the update landed (the installer itself is now fully hidden). winget and
; Scoop also run Setup silently, so a fresh install through them starts Gauge this way.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--updated"; Flags: nowait; Check: WizardSilent

[Code]
var
  InstallCompleted: Boolean;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallCompleted := True;
end;

// The in-app updater exits before a silent Setup can fail, and the [Run] relaunch above only
// happens on success. Without this a failed update leaves the user with no app and no message.
// {app} is unset if Setup stopped early, so the install folder is spelled out.
procedure DeinitializeSetup;
var
  ExePath: String;
  ResultCode: Integer;
begin
  if WizardSilent and not InstallCompleted then
  begin
    ExePath := ExpandConstant('{localappdata}\Programs\{#MyInstallName}\{#MyAppExeName}');
    if FileExists(ExePath) then
      Exec(ExePath, '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Gauge');
end;
