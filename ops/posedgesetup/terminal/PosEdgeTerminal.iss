; PosEdge multicaja - Terminal installer (PC2)
; Generates: PosEdge-Terminal-Setup.exe

#define MyAppName "PosEdge Multicaja Terminal"
#define MyAppVersion "0.1.0"

[Setup]
AppId={{8B7C5F6B-0D4B-41F4-9F5E-710E6D0A6C8A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={commonpf64}\PosEdgeTerminal
DefaultGroupName={#MyAppName}
OutputDir=out
OutputBaseFilename=PosEdge-Terminal-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
ShowLanguageDialog=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Files]
Source: "staging\Terminal\*";      DestDir: "{app}\Terminal";     Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\DiscoveryCli\*";  DestDir: "{app}\DiscoveryCli"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\TerminalExtras\*";DestDir: "{app}\TerminalExtras"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Abrir terminal"; Filename: "{app}\Terminal\PosEdge.Terminal.exe"
Name: "{group}\Reconectar multicaja"; Filename: "{app}\Terminal\PosEdge.Terminal.exe"
Name: "{group}\Resetear cache local"; Filename: "cmd.exe"; Parameters: "/c rmdir /s /q ""%LocalAppData%\PosEdge"""; Comment: "Borra cache local"

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Minimized -File ""{app}\TerminalExtras\posedgesetup-terminal.ps1"""; Flags: waituntilterminated; StatusMsg: "Conectando terminal a servidor multicaja..."
Filename: "{app}\Terminal\PosEdge.Terminal.exe"; Flags: nowait postinstall skipifsilent; Description: "Abrir terminal ahora"

