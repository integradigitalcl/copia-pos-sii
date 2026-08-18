; PosEdge multicaja - Server installer (PC1)
; Generates: PosEdge-Server-Setup.exe

#define MyAppName "PosEdge Multicaja Server"
#define MyAppVersion "0.1.0"

[Setup]
AppId={{E2A02B0A-67CF-4D63-8A2E-5A7FE8CE0E01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={commonpf64}\PosEdge
DefaultGroupName={#MyAppName}
OutputDir=out
OutputBaseFilename=PosEdge-Server-Setup
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
Source: "staging\Api\*";            DestDir: "{app}\Api";          Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\Workers\*";        DestDir: "{app}\Workers";      Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\Prerequisites\*";  DestDir: "{app}\Prerequisites";Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\ServerExtras\*";   DestDir: "{app}\ServerExtras"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Ver estado multicaja"; Filename: "{app}\Api\PosEdge.Api.exe"; Parameters: ""; Comment: "Servidor PosEdge"
Name: "{group}\Reiniciar servicios multicaja"; Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command ""Restart-Service PosEdgeApi,PosEdgeWorkers -Force"""; Comment: "Reinicia servicios"
Name: "{group}\Detener servidor multicaja"; Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command ""Stop-Service PosEdgeWorkers,PosEdgeApi -Force"""; Comment: "Detiene servicios"
Name: "{group}\Iniciar servidor multicaja"; Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command ""Start-Service PosEdgeApi,PosEdgeWorkers"""; Comment: "Inicia servicios"

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Minimized -File ""{app}\ServerExtras\posedgesetup-server.ps1"""; Flags: waituntilterminated; StatusMsg: "Configurando servidor multicaja (PostgreSQL + DB + servicios)..."

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command ""sc.exe stop PosEdgeApi ^& sc.exe delete PosEdgeApi ^& sc.exe stop PosEdgeWorkers ^& sc.exe delete PosEdgeWorkers"""; Flags: runhidden; RunOnceId: "RemoveServices"

