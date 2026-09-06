#define MyAppName "VoiceGuard"
#define MyAppVersion "6.6"
#define MyAppPublisher "Jack The Gooner"
#define MyAppExeName "VoiceGuard.exe"

[Setup]
AppId={{8D8D4B3E-0F5D-4A75-A3C8-7B5D4F5A6D51}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\\VoiceGuard\\6.6
DefaultGroupName=VoiceGuard
OutputDir=installer
OutputBaseFilename=VoiceGuard_Setup_6.6
Compression=lzma
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
UninstallDisplayIcon={app}\VoiceGuard.exe
WizardStyle=modern

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\\VoiceGuard"; Filename: "{app}\\{#MyAppExeName}"; IconFilename: "{app}\\{#MyAppExeName}"; IconIndex: 0
Name: "{commondesktop}\\VoiceGuard"; Filename: "{app}\\{#MyAppExeName}"; IconFilename: "{app}\\{#MyAppExeName}"; IconIndex: 0; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
Filename: "{app}\\{#MyAppExeName}"; Description: "Launch VoiceGuard"; Flags: nowait postinstall skipifsilent
