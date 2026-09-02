#ifndef Staging
  #define Staging "..\..\artifacts\web-installer"
#endif
#ifndef Configuration
  #define Configuration "Release"
#endif

[Setup]
AppId={{B68A0F9C-9A31-4F3F-9D50-4F2D1CFB7A61}
AppName=Grunflex POS Web
AppVersion=1.2.1
DefaultDirName={autopf}\GrunflexPOS Web
DefaultGroupName=Grunflex POS Web
OutputDir=out
OutputBaseFilename=GrunflexPOS-Web-Setup
Compression=lzma
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
SetupIconFile=..\posedgesetup\assets\grunflex-pos.ico
UninstallDisplayIcon={app}\grunflex-pos.ico
WizardStyle=modern
DisableProgramGroupPage=yes

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Types]
Name: "server"; Description: "Caja principal — POS Web + API multicaja (servidor en esta PC)."
Name: "client"; Description: "Caja adicional — POS Web conectado a la caja principal por red."

[Components]
Name: "web"; Description: "POS Web (Chrome) y Hardware Bridge"; Types: server client; Flags: fixed
Name: "api"; Description: "API multicaja en puerto 7279 (servicio Windows)"; Types: server; Flags: fixed
Name: "extras"; Description: "Scripts de servidor y firewall"; Types: server; Flags: fixed

[Files]
Source: "{#Staging}\web\*"; DestDir: "{app}\web"; Flags: recursesubdirs ignoreversion; Components: web
Source: "{#Staging}\bridge\*"; DestDir: "{app}\bridge"; Flags: recursesubdirs ignoreversion skipifsourcedoesntexist; Components: web
Source: "{#Staging}\API\*"; DestDir: "{app}\API"; Flags: recursesubdirs ignoreversion; Components: api
Source: "{#Staging}\ServerExtras\*"; DestDir: "{app}\ServerExtras"; Flags: recursesubdirs ignoreversion; Components: extras
Source: "{#Staging}\habilitar-firewall-multicaja.ps1"; DestDir: "{app}"; Flags: ignoreversion; Components: web
Source: "{#Staging}\run-web-production.ps1"; DestDir: "{app}"; Flags: ignoreversion; Components: web
Source: "{#Staging}\verify-golive.ps1"; DestDir: "{app}"; Flags: ignoreversion; Components: web
Source: "{#Staging}\ensure-server-ready.ps1"; DestDir: "{app}"; Flags: ignoreversion; Components: extras
Source: "{#Staging}\apply-install-profile.ps1"; DestDir: "{app}"; Flags: ignoreversion; Components: web
Source: "{#Staging}\ensure-multicaja-client.ps1"; DestDir: "{app}"; Flags: ignoreversion; Components: web
Source: "{#Staging}\discover-multicaja-server.ps1"; DestDir: "{app}"; Flags: ignoreversion; Components: web
Source: "{#Staging}\reset-multicaja-admin.ps1"; DestDir: "{app}"; Flags: ignoreversion; Components: web
Source: "{#Staging}\Tools\SyncMulticajaSettings\*"; DestDir: "{app}\Tools\SyncMulticajaSettings"; Flags: recursesubdirs ignoreversion skipifsourcedoesntexist; Components: web
Source: "{#Staging}\Tools\SeedAdmin\*"; DestDir: "{app}\Tools\SeedAdmin"; Flags: recursesubdirs ignoreversion skipifsourcedoesntexist; Components: api
Source: "{#Staging}\grunflex-pos.ico"; DestDir: "{app}"; Flags: ignoreversion; Components: web

[Dirs]
Name: "{commonappdata}\GrunflexPOS\Web"
Name: "{commonappdata}\GrunflexPOS\data"
Name: "{commonappdata}\GrunflexPOS\config"
Name: "{localappdata}\GrunflexPOS"
Name: "{localappdata}\GrunflexPOS\HardwareBridge"

[Icons]
Name: "{group}\Grunflex POS Web"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\run-web-production.ps1"" -PublishDirectory ""{app}\web"" -BridgeDirectory ""{app}\bridge"" -ShowCredentialsHint"; WorkingDir: "{app}"; IconFilename: "{app}\grunflex-pos.ico"
Name: "{commondesktop}\Grunflex POS Web"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\run-web-production.ps1"" -PublishDirectory ""{app}\web"" -BridgeDirectory ""{app}\bridge"" -ShowCredentialsHint"; WorkingDir: "{app}"; IconFilename: "{app}\grunflex-pos.ico"; Tasks: desktopicon
Name: "{group}\Verificar go-live"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\verify-golive.ps1"" -PublishRoot ""{app}"""; WorkingDir: "{app}"; IconFilename: "{app}\grunflex-pos.ico"
Name: "{group}\API multicaja (diagnóstico)"; Filename: "{app}\API\GrunflexPOS.API.exe"; Components: api; Comment: "Solo diagnóstico. En operación normal la API corre como servicio Windows."; IconFilename: "{app}\grunflex-pos.ico"

[Tasks]
Name: "desktopicon"; Description: "Crear acceso directo en el escritorio"; GroupDescription: "Accesos directos:"

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\apply-install-profile.ps1"" -Role server -AppRoot ""{app}"""; StatusMsg: "Configurando API multicaja y servicio Windows..."; Flags: waituntilterminated; Check: EsTipoServidor; Components: extras
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\apply-install-profile.ps1"" -Role client -AppRoot ""{app}"""; StatusMsg: "Buscando caja principal en la red y configurando conexión..."; Flags: waituntilterminated; Check: EsTipoCliente; Components: web
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\run-web-production.ps1"" -PublishDirectory ""{app}\web"" -BridgeDirectory ""{app}\bridge"" -ShowCredentialsHint"; Description: "Iniciar Grunflex POS Web + Hardware Bridge"; Flags: postinstall nowait skipifsilent runhidden; Components: web

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\ServerExtras\desinstalar-servicio-grunflexpos.ps1"""; Flags: runhidden; RunOnceId: "DesinstalarServicioApi"; Check: FileExists(ExpandConstant('{app}\ServerExtras\desinstalar-servicio-grunflexpos.ps1'))

[Code]
function EsTipoServidor: Boolean;
begin
  Result := (WizardSetupType(False) = 'server');
end;

function EsTipoCliente: Boolean;
begin
  Result := (WizardSetupType(False) = 'client');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectComponents then
  begin
    WizardForm.PageNameLabel.Caption := 'Tipo de instalación';
    WizardForm.PageDescriptionLabel.Caption := 'Caja principal: POS Web + API multicaja. Caja adicional: detecta la principal en la red, configura la conexión y verifica que responda antes de terminar.';
  end;
end;
