; Inno Setup script for Cassaforte
; Build with: iscc installer\cassaforte.iss

#define AppName "Cassaforte"
#define AppVersion "1.1.0"
#define AppPublisher "Luca"
#define AppExeName "vault.UI.exe"
#define BuildRoot "..\\artifacts\\publish\\win-x64"

[Setup]
AppId={{8CB57C60-8E9E-4F70-9C3A-98C1C7D9F73E}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir=..\artifacts\installer
OutputBaseFilename=Cassaforte-Setup-{#AppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
PrivilegesRequired=admin
SetupIconFile=..\icona.ico
ChangesAssociations=yes

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[Tasks]
Name: "desktopicon"; Description: "Crea icona sul desktop"; GroupDescription: "Icone aggiuntive:"; Flags: unchecked

[Files]
Source: "{#BuildRoot}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCR; Subkey: ".vault"; ValueType: string; ValueData: "Cassaforte.vault"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "Cassaforte.vault"; ValueType: string; ValueData: "Cassaforte Vault File"; Flags: uninsdeletekey
Root: HKCR; Subkey: "Cassaforte.vault\shell"; ValueType: string; ValueData: "open"
Root: HKCR; Subkey: "Cassaforte.vault\shell\open"; ValueType: string; ValueData: "Apri con Cassaforte"
Root: HKCR; Subkey: "Cassaforte.vault\DefaultIcon"; ValueType: string; ValueData: """{app}\{#AppExeName}"",0"
Root: HKCR; Subkey: "Cassaforte.vault\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExeName}"" ""%1"""
Root: HKCR; Subkey: "Applications\{#AppExeName}\SupportedTypes"; ValueName: ".vault"; ValueType: string; ValueData: ""
Root: HKCR; Subkey: "Applications\{#AppExeName}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExeName}"" ""%1"""

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Avvia {#AppName}"; Flags: nowait postinstall skipifsilent

