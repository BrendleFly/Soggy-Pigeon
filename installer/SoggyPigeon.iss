; Soggy Pigeon installer (Inno Setup 6)
; ====================================
; Per-user install, no admin prompt, dark wizard. Ships the self-contained
; win-x64 build (ReadyToRun), so a clean Windows 11 needs no .NET runtime.
;
; Build:
;   publish\make-installer.ps1        (publishes, then calls ISCC on this file)
;   or directly:
;   ISCC.exe /DMyAppVersion=0.2.0 installer\SoggyPigeon.iss
;
; The app checks for updates itself against the public GitHub Releases feed;
; this installer only lays the files down and registers the shortcuts.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#define MyAppName "Soggy Pigeon"
#define MyAppNameEn "Soggy Pigeon"
#define MyAppExeName "SoggyPigeon.exe"
#define MyAppPublisher "BrendleFly"
#define MyAppURL "https://github.com/BrendleFly/Soggy-Pigeon"
#define MyAppSourceDir "..\publish\app-win"

[Setup]
; NOTE: The AppId uniquely identifies this application and is used by Windows
; to recognise upgrades. Do not change it across versions, or every player
; gets a second install rather than an upgrade.
AppId={{8F3C1D2A-7B64-4E59-9A0D-2C5E7F1B8A34}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} installer

; Per-user install: no UAC prompt, installs under %LOCALAPPDATA%\Programs.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
DisableWelcomePage=no
DisableReadyPage=no

; x64 only: the app ships an x64 SDL2.dll and a Windows-only input stack.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Dark wizard. 'modern' is the new wizard style; 'dynamic' follows the
; Windows app theme so the installer is dark on a dark system. The engine
; picks ISetup-dark / SetupCustomStyle automatically.
WizardStyle=modern dynamic
WizardBackColor=#1b1b1f
WizardBackColorDynamicDark=#1b1b1f
WizardImageStretch=no

OutputDir=..\installer-output
OutputBaseFilename=SoggyPigeon-Setup-{#MyAppVersion}
SetupIconFile=
Compression=lzma2/max
SolidCompression=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

; Close the app before upgrading it, otherwise the file copy fails on Windows.
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#MyAppSourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Remember the install location so a later update can find the old files even
; if the player moved the shortcut. Root=HKCU because this is a per-user app.
Root: HKCU; Subkey: "Software\{#MyAppPublisher}\{#MyAppName}"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"; Flags: uninsdeletekey

[Code]
// The wizard is dark when Windows is (WizardStyle=modern dynamic) and the
// colours above follow it. Nothing else needs scripting: the install is a
// straight file copy plus shortcuts, and [Setup]'s CloseApplications asks
// Windows to close a running app before overwriting it.
//
// One deliberate omission: a running GAME is not something to kill silently
// mid-match. CloseApplicationsFilter above covers *.exe/*.dll for the app
// itself; if a game holds a file this installer needs, Windows reports it
// rather than us terminating someone's session.
function InitializeSetup(): Boolean;
begin
  Result := True;
end;