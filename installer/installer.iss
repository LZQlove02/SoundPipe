; Inno Setup script for SoundPipe desktop app
; Build via installer\build-installer.ps1 (expects .\publish\SoundPipe.exe)

#define MyAppName "SoundPipe"
#define MyAppVersion "1.0.2"
#define MyAppExeName "SoundPipe.exe"
#define MyAppPublisher "SoundPipe"
#define MyAppURL "https://vb-audio.com/Cable/"

[Setup]
AppId={{D4A2B9F1-3C5E-4B7A-9F2D-8E1C6A0B5C43}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\SoundPipe
DefaultGroupName=SoundPipe
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=dist
OutputBaseFilename=SoundPipe-Setup-{#MyAppVersion}
SetupIconFile=..\SoundPipe.App\Assets\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
PrivilegesRequired=admin
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autorun"; Description: "Start SoundPipe automatically at Windows login (runs in tray)"; Flags: unchecked

[Files]
Source: "publish\SoundPipe.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\SoundPipe"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall SoundPipe"; Filename: "{uninstallexe}"
Name: "{autodesktop}\SoundPipe"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Auto-run: same HKCU Run value the in-app toggle manages (single source of truth = registry)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SoundPipe"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: autorun; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 仅清理日志子目录，保留用户配置 settings.json（%APPDATA%\SoundPipe\settings.json）
Type: filesandordirs; Name: "{userappdata}\SoundPipe\logs"
