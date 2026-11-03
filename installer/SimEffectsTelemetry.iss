; SimEffects Telemetry installer (Inno Setup 6). Built by tools\release.ps1, which publishes the app first and passes
; the version: ISCC /DAppVersion=0.2.0 installer\SimEffectsTelemetry.iss
; The in-app updater runs it with /SILENT; it then starts the app again (the [Run] entry runs in silent mode too).

#ifndef AppVersion
  #error Pass the version: /DAppVersion=x.y.z
#endif

[Setup]
; Never change AppId: it's how an update finds the copy already installed.
AppId={{BD95121E-871B-4FB6-8359-70DB5E515162}
AppName=SimEffects Telemetry
AppVersion={#AppVersion}
AppVerName=SimEffects Telemetry {#AppVersion}
AppPublisher=Ross Carlson
AppPublisherURL=https://github.com/rosscarlson/SimEffects-Telemetry
AppSupportURL=https://github.com/rosscarlson/SimEffects-Telemetry/issues
AppUpdatesURL=https://github.com/rosscarlson/SimEffects-Telemetry/releases
DefaultDirName={autopf}\SimEffects\Telemetry
DefaultGroupName=SimEffects
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish\installer
OutputBaseFilename=SimEffectsTelemetry-Setup-{#AppVersion}
SetupIconFile=..\src\GtTelemetry.App\Assets\App.ico
UninstallDisplayIcon={app}\SimEffectsTelemetry.exe
UninstallDisplayName=SimEffects Telemetry
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\publish\SimEffectsTelemetry.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\data\*"; DestDir: "{app}\data"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\publish\sessions\*"; DestDir: "{app}\sessions"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\SimEffects Telemetry"; Filename: "{app}\SimEffectsTelemetry.exe"
Name: "{autodesktop}\SimEffects Telemetry"; Filename: "{app}\SimEffectsTelemetry.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SimEffectsTelemetry.exe"; Description: "Start SimEffects Telemetry"; Flags: nowait postinstall
