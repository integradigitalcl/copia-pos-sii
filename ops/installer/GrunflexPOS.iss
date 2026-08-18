; Grunflex POS - Instalador comercial (POS + API embebido + multicaja automatica).
; 1) .\build-staging.ps1   2) ISCC  o  .\compile-installer.ps1

#define MyAppName "Grunflex POS"
#define MyAppVersion "1.3.24"
#define MyAppPublisher "Grunflex"
#define MyAppExeName "GrunflexPOS2.exe"
#define MyApiExeName "GrunflexPOS.API.exe"

[Setup]
AppId={{E7A63F8B-4C91-4F2D-9A88-2B1C0D9E8F7A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (C) {#MyAppPublisher}
DefaultDirName={commonpf64}\GrunflexPOS
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=no
DisableWelcomePage=yes
OutputDir=out
OutputBaseFilename=GrunflexPOS_Setup_{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
LZMANumBlockThreads=2
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=LICENCIA.txt
ShowLanguageDialog=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Types]
Name: "server"; Description: "Caja Principal - POS + API + recurso compartido (multicaja servidor)."
Name: "client"; Description: "Caja Adicional - POS conectado a la caja principal por red."

[Components]
Name: "pos";    Description: "Interfaz punto de venta (WPF) y base SQLite integrada"; Types: server client; Flags: fixed
Name: "api";    Description: "API ASP.NET Core (misma carpeta, sin instalar runtime aparte)"; Types: server; Flags: fixed
Name: "extras"; Description: "Scripts de servidor y documentación"; Types: server; Flags: fixed

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos directos adicionales:"
Name: "wipedata";    Description: "Borrar TODOS los datos existentes (usuarios, cajas, ventas, licencia, BD) y empezar desde cero. ATENCION: irreversible."; Flags: unchecked; GroupDescription: "Reset de fabrica:"

[InstallDelete]
; Limpia configuraciones de terminal previas para que la instalación nueva arranque
; limpia. Se borra TANTO el archivo per-user (LocalAppData) COMO el machine-wide
; (CommonAppData / ProgramData), porque cuando el instalador corre elevado como admin
; {localappdata} puede resolver al perfil equivocado; el de CommonAppData siempre es
; correcto y es el que el POS lee con prioridad a partir de la versión 1.3.2+.
Type: files; Name: "{app}\appsettings.local.json"
Type: files; Name: "{app}\grunflex-terminal.json"
Type: files; Name: "{app}\grunflex-terminal.json.aplicado"
Type: files; Name: "{localappdata}\GrunflexPOS\config\appsettings.local.json"
Type: files; Name: "{commonappdata}\GrunflexPOS\config\appsettings.local.json"
Type: files; Name: "{commonappdata}\GrunflexPOS\config\.multicaja-installer-host"

[Files]
Source: "staging\POS\*";          DestDir: "{app}";              Flags: ignoreversion recursesubdirs createallsubdirs; Components: pos
Source: "staging\API\*";          DestDir: "{app}\API";          Flags: ignoreversion recursesubdirs createallsubdirs; Components: api
Source: "staging\ServerExtras\*"; DestDir: "{app}\ServerExtras"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: extras
Source: "habilitar-firewall-multicaja.ps1"; DestDir: "{app}";   Flags: ignoreversion; Components: pos
Source: "staging\Prerequisites\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\{#MyAppName}";              Filename: "{app}\{#MyAppExeName}"; Components: pos
Name: "{group}\Iniciar API (consola/diagnostico)"; Filename: "{app}\API\{#MyApiExeName}"; Components: api; Comment: "Solo para diagnostico manual. En operacion normal corre como servicio Windows."
Name: "{autodesktop}\{#MyAppName}";        Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; Components: pos

[Run]
; WebView2: requerido por el POS para vistas web (Servipag, soporte online).
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; Flags: waituntilterminated; StatusMsg: "Instalando Microsoft WebView2 (vistas web integradas)..."; Check: WebView2NeedsInstall

; Caja Principal: script que crea el share, abre firewall, migra datos legacy y registra el servicio API.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Minimized -File ""{app}\ServerExtras\habilitar-recurso-grunflexpos.ps1"""; Flags: waituntilterminated; StatusMsg: "Configurando multicaja y servicio Windows (puede tardar hasta 60s)..."; Check: EsTipoServidor; Components: extras
; Refuerzo: reglas entrantes API/SMB/discovery + reintento de inicio del servicio (por si el paso anterior quedó a medias).
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\habilitar-firewall-multicaja.ps1"" -Role Server"; Flags: waituntilterminated runhidden; StatusMsg: "Aplicando reglas de firewall API (7279)..."; Check: EsTipoServidor; Components: pos

; Caja Adicional: firewall saliente SMB + API + discovery (script único, mismo criterio que la principal).
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\habilitar-firewall-multicaja.ps1"" -Role Client"; Flags: waituntilterminated runhidden; StatusMsg: "Aplicando reglas de firewall multicaja (cliente)..."; Check: EsTipoCliente; Components: pos

; Caja Adicional (sin ServerExtras en disco): política SMB + mapeo persistente con credenciales por defecto.
; El ejecutable también conecta vía WNet al arrancar (appsettings.local.json con SmbShare*).
Filename: "{sys}\reg.exe"; Parameters: "ADD HKLM\SOFTWARE\Policies\Microsoft\Windows\LanmanWorkstation /v AllowInsecureGuestAuth /t REG_DWORD /d 1 /f"; Flags: waituntilterminated runhidden; Check: EsTipoCliente
; Tras copiar archivos: asistente visual (busqueda LAN + IP manual) escribe appsettings y marca de host para net use.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--installer-conectar-servidor"; WorkingDir: "{app}"; Flags: waituntilterminated; StatusMsg: "Buscando la caja principal en la red y aplicando la conexion..."; Check: EsTipoClienteYNoSilencioso; Components: pos
Filename: "{sys}\cmd.exe"; Parameters: "/c net use {code:GetServerUncIpc} GrunflexLan2025SMB /user:{code:GetServerSmbUser} /persistent:no"; Flags: waituntilterminated runhidden; Check: EsTipoClienteConIp
Filename: "{sys}\cmd.exe"; Parameters: "/c net use {code:GetServerUncShare} GrunflexLan2025SMB /user:{code:GetServerSmbUser} /persistent:yes"; Flags: waituntilterminated runhidden; Check: EsTipoClienteConIp

[UninstallRun]
; Detener+borrar servicio antes de remover archivos (de lo contrario el .exe queda bloqueado).
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\ServerExtras\desinstalar-servicio-grunflexpos.ps1"""; Flags: runhidden; RunOnceId: "DesinstalarServicio"; Check: FileExists(ExpandConstant('{app}\ServerExtras\desinstalar-servicio-grunflexpos.ps1'))

[Code]
var
  ServerPage: TInputQueryWizardPage;

function WebView2RuntimePresent: Boolean;
var
  s: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM64, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', s) and (Trim(s) <> '') then
    Result := True
  else if RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', s) and (Trim(s) <> '') then
    Result := True
  else if RegQueryStringValue(HKCU64, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', s) and (Trim(s) <> '') then
    Result := True
  else if RegQueryStringValue(HKCU32, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', s) and (Trim(s) <> '') then
    Result := True;
end;

function WebView2NeedsInstall: Boolean;
begin
  Result := not WebView2RuntimePresent;
end;

function EsTipoCliente: Boolean;
begin
  Result := (WizardSetupType(False) = 'client');
end;

function EsTipoServidor: Boolean;
begin
  Result := (WizardSetupType(False) = 'server');
end;

function EsTipoClienteYNoSilencioso: Boolean;
begin
  Result := EsTipoCliente and (not WizardSilent());
end;

function ReadInstallerHostMarker: String;
var
  lines: TArrayOfString;
begin
  Result := '';
  if not LoadStringsFromFile(ExpandConstant('{commonappdata}\GrunflexPOS\config\.multicaja-installer-host'), lines) then
    Exit;
  if GetArrayLength(lines) < 1 then
    Exit;
  Result := Trim(lines[0]);
end;

function GetServerIp(Param: string): String;
begin
  Result := ReadInstallerHostMarker;
  if Result <> '' then
    Exit;
  if Assigned(ServerPage) then
    Result := Trim(ServerPage.Values[0]);
end;

function EsTipoClienteConIp: Boolean;
begin
  // Host del servidor: lo escribe el asistente POS tras la busqueda, o el campo opcional del wizard.
  Result := EsTipoCliente and (Trim(GetServerIp('')) <> '');
end;

function GetServerUncShare(Param: string): String;
var
  ip: String;
begin
  ip := GetServerIp(Param);
  Result := Chr(92) + Chr(92) + ip + Chr(92) + 'GrunflexPOS';
end;

function GetServerSmbUser(Param: string): String;
var
  ip: String;
begin
  ip := GetServerIp(Param);
  Result := ip + Chr(92) + 'grunflexshare';
end;

function GetServerUncIpc(Param: string): String;
var
  ip: String;
begin
  ip := GetServerIp(Param);
  Result := Chr(92) + Chr(92) + ip + Chr(92) + 'IPC$';
end;

procedure InitializeWizard;
begin
  ServerPage := CreateInputQueryPage(wpSelectComponents,
    'Conexion multicaja (opcional)',
    'Campo opcional: IP o nombre del servidor',
    'Si ya conoce la IP o el nombre del PC de la caja principal, puede escribirlo aqui (ayuda a cachear el acceso SMB).' + #13#10 +
    'Si lo deja en blanco, no hay problema: al finalizar la copia de archivos se abrira un asistente que busca la caja principal en la red (estilo Eleventa).' + #13#10 +
    'Ese paso guarda la conexion para que el POS ya arranque enlazado.');
  ServerPage.Add('Servidor (IP o nombre, opcional):', False);
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
  if CurPageID = wpSelectComponents then
  begin
    WizardForm.PageNameLabel.Caption := 'Tipo de instalacion';
    WizardForm.PageDescriptionLabel.Caption := 'Caja principal aloja la base y la API; la caja adicional se conecta por red. La base usa SQLite (sin PostgreSQL).';
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  // IP del servidor es OPCIONAL en el instalador. Si queda vacia, el usuario
  // podra completarla luego desde el POS (Configuracion -> Administrar caja
  // -> "Conectar a caja principal") usando auto-discovery LAN.
end;

procedure EscribirAppSettingsLocalServidor;
var
  ruta, rutaUser, contenido, cs, apiBase, pagoBase: string;
  dir, dirUser: string;
begin
  dir := ExpandConstant('{commonappdata}\GrunflexPOS\config');
  ForceDirectories(dir);
  ruta := dir + '\appsettings.local.json';

  cs       := 'Data Source=' + ExpandConstant('{commonappdata}\GrunflexPOS\data\grunflex.db') + ';Cache=Shared';
  apiBase  := 'http://127.0.0.1:7279/';
  pagoBase := 'http://127.0.0.1:7279/api/pago';

  StringChangeEx(cs, '\', '\\', True);

  contenido :=
    '{' + #13#10 +
    '  "ConnectionStrings": { "Default": "' + cs + '" },' + #13#10 +
    '  "Api": {' + #13#10 +
    '    "BaseUrl": "' + apiBase + '",' + #13#10 +
    '    "PagoBaseUrl": "' + pagoBase + '"' + #13#10 +
    '  },' + #13#10 +
    '  "CajaId": "",' + #13#10 +
    '  "TerminalRole": "server"' + #13#10 +
    '}' + #13#10;

  SaveStringToFile(ruta, contenido, False);

  dirUser := ExpandConstant('{localappdata}\GrunflexPOS\config');
  ForceDirectories(dirUser);
  rutaUser := dirUser + '\appsettings.local.json';
  SaveStringToFile(rutaUser, contenido, False);
end;

procedure BorrarDatosExistentes;
var
  base, baseUser: string;
begin
  // Reset de fabrica: borra BD, logs, licencia y config. Despues de esto la siguiente
  // ejecucion del POS arranca como instalacion virgen ("Primer usuario administrador").
  base := ExpandConstant('{commonappdata}\GrunflexPOS');
  if DirExists(base + '\data') then DelTree(base + '\data', True, True, True);
  if DirExists(base + '\logs') then DelTree(base + '\logs', True, True, True);
  if DirExists(base + '\backups') then DelTree(base + '\backups', True, True, True);
  if DirExists(base + '\config') then DelTree(base + '\config', True, True, True);
  if DirExists(base + '\queue') then DelTree(base + '\queue', True, True, True);
  if DirExists(base + '\license') then DelTree(base + '\license', True, True, True);

  baseUser := ExpandConstant('{localappdata}\GrunflexPOS');
  if DirExists(baseUser) then DelTree(baseUser, True, True, True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    // Si el operador marco el task de reset, lo hacemos ANTES de copiar archivos para
    // que no se borre lo que el instalador acaba de poner.
    if WizardIsTaskSelected('wipedata') then
      BorrarDatosExistentes;
  end;

  if CurStep = ssPostInstall then
  begin
    if EsTipoServidor then
      EscribirAppSettingsLocalServidor;
  end;
end;
