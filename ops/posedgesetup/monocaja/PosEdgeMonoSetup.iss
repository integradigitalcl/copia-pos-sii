; Grunflex POS - Instalador mono caja (un solo equipo, sin terminal adicional)
; Genera: GrunflexPOS-Mono-Setup.exe
; Usa el mismo payload que ops/posedgesetup/single/staging

#define MyAppName "Grunflex POS"
#define MyAppVersion "0.3.5"
#define MyAppEdition "Mono caja"
#define MyLauncher "PosEdgeLauncher.exe"

[Setup]
AppId={{A91C4E2B-6F3D-4A8E-9C1B-5D2E8F0A1B3C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion} ({#MyAppEdition})
DefaultDirName={commonpf64}\PosEdge
DefaultGroupName={#MyAppName}
OutputDir=out
OutputBaseFilename=GrunflexPOS-Mono-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
ShowLanguageDialog=no
DisableProgramGroupPage=no
SetupIconFile=..\assets\grunflex-pos.ico
UninstallDisplayIcon={app}\GrunflexPOS\GrunflexPOS2.exe

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[InstallDelete]
Type: files; Name: "{commonappdata}\GrunflexPOS\config\appsettings.local.json"
Type: files; Name: "{localappdata}\GrunflexPOS\config\appsettings.local.json"
Type: files; Name: "{app}\GrunflexPOS\appsettings.local.json"
Type: files; Name: "{commondesktop}\Grunflex POS.lnk"
Type: files; Name: "{userdesktop}\Grunflex POS.lnk"

[Files]
Source: "..\single\staging\GrunflexPOS\*";     DestDir: "{app}\GrunflexPOS";     Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\single\staging\GrunflexApi\*";     DestDir: "{app}\GrunflexApi";     Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\single\staging\Launcher\*";        DestDir: "{app}\Launcher";        Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\single\staging\Api\*";             DestDir: "{app}\Api";             Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\single\staging\Workers\*";         DestDir: "{app}\Workers";         Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\single\staging\DiscoveryCli\*";    DestDir: "{app}\DiscoveryCli";    Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\single\staging\Bootstrapper\*";    DestDir: "{app}\Bootstrapper";    Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\single\staging\SetupAgent\*";      DestDir: "{app}\SetupAgent";      Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\single\staging\Guardian\*";        DestDir: "{app}\Guardian";        Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\single\staging\DiagCli\*";         DestDir: "{app}\DiagCli";         Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\single\staging\SelfCheckCli\*";    DestDir: "{app}\SelfCheckCli";    Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\single\staging\BackupCli\*";       DestDir: "{app}\BackupCli";       Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\single\staging\Prerequisites\postgresql-windows-x64.exe"; DestDir: "{app}\Prerequisites"; Flags: ignoreversion solidbreak nocompression
Source: "..\single\staging\Prerequisites\*";   DestDir: "{app}\Prerequisites";   Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\single\staging\SetupExtras\*";     DestDir: "{app}\SetupExtras";     Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\installer\habilitar-firewall-multicaja.ps1"; DestDir: "{app}\SetupExtras"; Flags: ignoreversion
Source: "..\single\staging\DevTerminal\*";     DestDir: "{app}\_dev\Terminal";    Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\single\staging\Prerequisites\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: WebView2NeedsInstall

[Icons]
Name: "{group}\Abrir Grunflex POS"; Filename: "{app}\Launcher\{#MyLauncher}"; Comment: "Punto de venta (mono caja)"
Name: "{group}\Reparar sistema"; Filename: "{app}\Launcher\{#MyLauncher}"; Parameters: "--repair"; Comment: "Recuperación automática"
Name: "{group}\Self-check"; Filename: "{app}\SelfCheckCli\PosEdge.SelfCheckCli.exe"; Comment: "Verificación interna"
Name: "{group}\Exportar diagnóstico"; Filename: "{app}\DiagCli\PosEdge.DiagCli.exe"; Comment: "Soporte técnico"
Name: "{group}\Backup ahora"; Filename: "{app}\BackupCli\PosEdge.BackupCli.exe"; Parameters: "--kind daily"

#include "..\shared\desktop-icons.iss"

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; Flags: waituntilterminated runhidden; StatusMsg: "Preparando componentes..."; Check: WebView2NeedsInstall

Filename: "{app}\Bootstrapper\PosEdge.Bootstrapper.exe"; Parameters: "--payload-root ""{app}"""; Flags: waituntilterminated runhidden; StatusMsg: "Validando instalación..."

Filename: "{app}\SetupAgent\PosEdge.SetupAgent.exe"; Parameters: "--payload-root ""{app}"""; Flags: waituntilterminated runhidden; StatusMsg: "Configurando sistema..."

Filename: "{app}\SelfCheckCli\PosEdge.SelfCheckCli.exe"; Flags: waituntilterminated runhidden; StatusMsg: "Validando sistema..."

Filename: "{app}\Launcher\{#MyLauncher}"; Parameters: "--payload-root ""{app}"" --post-install"; WorkingDir: "{app}\Launcher"; Flags: nowait postinstall skipifsilent; Description: "Abrir Grunflex POS ahora"; StatusMsg: "Iniciando punto de venta..."

[UninstallRun]
Filename: "cmd.exe"; Parameters: "/c sc.exe stop PosEdgeApi & sc.exe delete PosEdgeApi & sc.exe stop PosEdgeWorkers & sc.exe delete PosEdgeWorkers & sc.exe stop PosEdgeGuardian & sc.exe delete PosEdgeGuardian & sc.exe stop GrunflexPOSAPI & sc.exe delete GrunflexPOSAPI"; Flags: runhidden; RunOnceId: "RemoveServices"

[Code]
var
  WipeDataPage: TWizardPage;
  WipeDataCheck: TNewCheckBox;

procedure InitializeWizard;
begin
  WipeDataPage := CreateCustomPage(
    wpSelectDir,
    'Datos de instalaciones anteriores',
    'Si al reinstalar siguen apareciendo usuarios, productos o ventas antiguas, puede borrar los datos locales antes de continuar.' + #13#10 + #13#10 +
    'Esta acción no se puede deshacer.');

  WipeDataCheck := TNewCheckBox.Create(WipeDataPage);
  WipeDataCheck.Parent := WipeDataPage.Surface;
  WipeDataCheck.Caption := 'Borrar datos de Grunflex POS (usuarios, productos, inventario, ventas y configuración local)';
  WipeDataCheck.Left := ScaleX(0);
  WipeDataCheck.Top := ScaleY(24);
  WipeDataCheck.Width := WipeDataPage.SurfaceWidth;
  WipeDataCheck.Checked := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  cfgDir, rolePath, monoPath: String;
begin
  Result := '';
  cfgDir := ExpandConstant('{commonappdata}\PosEdge\config');
  ForceDirectories(cfgDir);

  if WipeDataCheck.Checked then
  begin
    rolePath := cfgDir + '\install-wipe-data.flag';
    SaveStringToFile(rolePath, '1', False);
  end;

  rolePath := cfgDir + '\install-forced-role.txt';
  SaveStringToFile(rolePath, 'server', False);

  monoPath := cfgDir + '\install-monocaja.flag';
  SaveStringToFile(monoPath, '1', False);
end;

function WebView2RuntimePresent: Boolean;
var
  s: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM64, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', s) and (Trim(s) <> '') then
    Result := True
  else if RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', s) and (Trim(s) <> '') then
    Result := True;
end;

function WebView2NeedsInstall: Boolean;
begin
  Result := not WebView2RuntimePresent;
end;
