#ifndef PublishDir
  #define PublishDir "..\dist\technician"
#endif

#ifndef OutputDir
  #define OutputDir "..\dist\installer"
#endif

#define MyAppName "Remote Assist Technician"
#define MyAppVersion "0.10.0-preview"
#define MyAppPublisher "Remote Assist"
#define MyAppExeName "RemoteAssistTechnician.exe"

[Setup]
AppId={{36A94D17-FB0A-4CE1-9131-BCF6562BCE29}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Remote Assist\Support Technician
DefaultGroupName=Remote Assist
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=Remote-Assist-Technician-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Remote Assist Technician"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Remote Assist Technician"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCR; Subkey: "remote-assist-tech"; ValueType: string; ValueName: ""; ValueData: "URL:Remote Assist Technician"; Flags: uninsdeletekey
Root: HKCR; Subkey: "remote-assist-tech"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCR; Subkey: "remote-assist-tech\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCR; Subkey: "remote-assist-tech\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Remote Assist Technician"; Flags: nowait postinstall skipifsilent
