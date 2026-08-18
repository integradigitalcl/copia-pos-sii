; PosEdge + Grunflex POS - Instalador multicaja (principal + adicional)
; Genera: PosEdge-Setup.exe

#define MyAppName "Grunflex POS"
#define MyAppVersion "0.3.10"
#define MyAppEdition "Multicaja"
#define MyLauncher "PosEdgeLauncher.exe"

[Setup]
AppId={{0B42D1C8-4D49-4D5B-8F1A-2B2B6B0A6AC1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion} ({#MyAppEdition})
DefaultDirName={commonpf64}\PosEdge
DefaultGroupName={#MyAppName}
OutputDir=out
OutputBaseFilename=PosEdge-Setup
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
Source: "staging\GrunflexPOS\*";     DestDir: "{app}\GrunflexPOS";     Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\GrunflexApi\*";     DestDir: "{app}\GrunflexApi";     Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\Launcher\*";        DestDir: "{app}\Launcher";        Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\Api\*";             DestDir: "{app}\Api";             Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\Workers\*";         DestDir: "{app}\Workers";         Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\DiscoveryCli\*";    DestDir: "{app}\DiscoveryCli";    Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\Bootstrapper\*";    DestDir: "{app}\Bootstrapper";    Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\SetupAgent\*";      DestDir: "{app}\SetupAgent";      Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\Guardian\*";        DestDir: "{app}\Guardian";        Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\DiagCli\*";         DestDir: "{app}\DiagCli";         Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\SelfCheckCli\*";    DestDir: "{app}\SelfCheckCli";    Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\BackupCli\*";       DestDir: "{app}\BackupCli";       Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\Prerequisites\postgresql-windows-x64.exe"; DestDir: "{app}\Prerequisites"; Flags: ignoreversion solidbreak nocompression
Source: "staging\Prerequisites\*";   DestDir: "{app}\Prerequisites";   Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\SetupExtras\*";     DestDir: "{app}\SetupExtras";     Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\installer\habilitar-firewall-multicaja.ps1"; DestDir: "{app}\SetupExtras"; Flags: ignoreversion
; Herramientas de desarrollo (sin accesos directos)
Source: "staging\DevTerminal\*";     DestDir: "{app}\_dev\Terminal";    Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\Prerequisites\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: WebView2NeedsInstall

[Icons]
Name: "{group}\Abrir Grunflex POS"; Filename: "{app}\Launcher\{#MyLauncher}"; Comment: "Punto de venta"
Name: "{group}\Reparar sistema"; Filename: "{app}\Launcher\{#MyLauncher}"; Parameters: "--repair"; Comment: "Recuperación automática"
Name: "{group}\Self-check"; Filename: "{app}\SelfCheckCli\PosEdge.SelfCheckCli.exe"; Comment: "Verificación interna"
Name: "{group}\Exportar diagnóstico"; Filename: "{app}\DiagCli\PosEdge.DiagCli.exe"; Comment: "Soporte técnico"
Name: "{group}\Backup ahora"; Filename: "{app}\BackupCli\PosEdge.BackupCli.exe"; Parameters: "--kind daily"

#include "..\shared\desktop-icons.iss"

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; Flags: waituntilterminated runhidden; StatusMsg: "Preparando componentes..."; Check: WebView2NeedsInstall

Filename: "{app}\Bootstrapper\PosEdge.Bootstrapper.exe"; Parameters: "--payload-root ""{app}"""; Flags: waituntilterminated runhidden; StatusMsg: "Validando instalación..."

Filename: "{app}\SetupAgent\PosEdge.SetupAgent.exe"; Parameters: "--payload-root ""{app}"""; Flags: waituntilterminated runhidden; StatusMsg: "Configurando sistema..."

Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\SetupExtras\habilitar-firewall-multicaja.ps1"" -Role Server"; Flags: waituntilterminated runhidden; StatusMsg: "Abriendo firewall multicaja (TCP 7279)..."; Check: EsTipoServidor

Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\SetupExtras\habilitar-firewall-multicaja.ps1"" -Role Client"; Flags: waituntilterminated runhidden; StatusMsg: "Abriendo firewall multicaja (cliente → 7279)..."; Check: EsTipoCliente

Filename: "{app}\SelfCheckCli\PosEdge.SelfCheckCli.exe"; Flags: waituntilterminated runhidden; StatusMsg: "Validando sistema..."

Filename: "{app}\Launcher\{#MyLauncher}"; Parameters: "--payload-root ""{app}"" --post-install"; WorkingDir: "{app}\Launcher"; Flags: nowait postinstall skipifsilent; Description: "Abrir Grunflex POS ahora"; StatusMsg: "Iniciando punto de venta..."

[UninstallRun]
Filename: "cmd.exe"; Parameters: "/c sc.exe stop PosEdgeApi & sc.exe delete PosEdgeApi & sc.exe stop PosEdgeWorkers & sc.exe delete PosEdgeWorkers & sc.exe stop PosEdgeGuardian & sc.exe delete PosEdgeGuardian & sc.exe stop GrunflexPOSAPI & sc.exe delete GrunflexPOSAPI"; Flags: runhidden; RunOnceId: "RemoveServices"

[Code]
var
  RolePage: TWizardPage;
  RoleServerRadio: TNewRadioButton;
  RoleClientRadio: TNewRadioButton;
  InstallRole: String;
  WipeDataPage: TWizardPage;
  WipeDataCheck: TNewCheckBox;
  ServerPage: TInputQueryWizardPage;

function EsTipoServidor: Boolean;
begin
  Result := (InstallRole = 'server');
end;

function EsTipoCliente: Boolean;
begin
  Result := (InstallRole = 'client');
end;

procedure RoleRadioClick(Sender: TObject);
begin
  if RoleServerRadio.Checked then
    InstallRole := 'server'
  else
    InstallRole := 'client';
end;

procedure InitializeWizard;
begin
  InstallRole := 'server';

  RolePage := CreateCustomPage(
    wpSelectDir,
    'Tipo de instalación',
    'Elija si este equipo será la caja principal (servidor multicaja) o una caja adicional conectada por red.');

  RoleServerRadio := TNewRadioButton.Create(RolePage);
  RoleServerRadio.Parent := RolePage.Surface;
  RoleServerRadio.Caption := 'Caja principal — aloja la base de datos y la API (servidor multicaja)';
  RoleServerRadio.Left := ScaleX(0);
  RoleServerRadio.Top := ScaleY(16);
  RoleServerRadio.Width := RolePage.SurfaceWidth;
  RoleServerRadio.Checked := True;
  RoleServerRadio.OnClick := @RoleRadioClick;

  RoleClientRadio := TNewRadioButton.Create(RolePage);
  RoleClientRadio.Parent := RolePage.Surface;
  RoleClientRadio.Caption := 'Caja adicional — se conecta a la caja principal por red (API multicaja)';
  RoleClientRadio.Left := ScaleX(0);
  RoleClientRadio.Top := ScaleY(48);
  RoleClientRadio.Width := RolePage.SurfaceWidth;
  RoleClientRadio.OnClick := @RoleRadioClick;

  WipeDataPage := CreateCustomPage(
    RolePage.ID,
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

  ServerPage := CreateInputQueryPage(WipeDataPage.ID,
    'Servidor multicaja (caja adicional)',
    'IP o nombre del PC de la caja principal',
    'Indique la IP o nombre del equipo donde instaló la caja principal (ej. 192.168.1.10).' + #13#10 +
    'Si lo deja vacío, el instalador intentará encontrarlo en la red automáticamente.');
  ServerPage.Add('Servidor (IP o nombre):', False);
  ServerPage.Values[0] := '';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if Assigned(ServerPage) and (PageID = ServerPage.ID) then
    Result := not EsTipoCliente;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if Assigned(RolePage) and (CurPageID = RolePage.ID) then
    RoleRadioClick(nil);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  cfgDir, rolePath, hostPath, hostVal: String;
begin
  Result := '';
  RoleRadioClick(nil);
  cfgDir := ExpandConstant('{commonappdata}\PosEdge\config');
  ForceDirectories(cfgDir);

  if WipeDataCheck.Checked then
  begin
    rolePath := cfgDir + '\install-wipe-data.flag';
    SaveStringToFile(rolePath, '1', False);
  end;

  rolePath := cfgDir + '\install-forced-role.txt';
  if EsTipoServidor then
    SaveStringToFile(rolePath, 'server', False)
  else
    SaveStringToFile(rolePath, 'terminal', False);

  if EsTipoCliente and Assigned(ServerPage) then
  begin
    hostVal := Trim(ServerPage.Values[0]);
    if hostVal <> '' then
    begin
      hostPath := cfgDir + '\install-server-host.txt';
      SaveStringToFile(hostPath, hostVal, False);
    end;
  end;
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
