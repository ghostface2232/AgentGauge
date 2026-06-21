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
; Silent update (in-app updater runs Setup with /SILENT): there is no finished
; page, so relaunch Gauge automatically once the files are in place.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: WizardSilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Gauge');
end;
