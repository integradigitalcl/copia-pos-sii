; Grunflex – License Issuer (INTERNO)
; Instalador EXCLUSIVO para el editor: NO se vende ni se distribuye a clientes.
; Incluye Grunflex.LicenseIssuer (emisor) y GrunflexPOS.LicenseManager (administrador local).
;
; 1) .\build-staging.ps1
; 2) .\compile.ps1   (o abrir el .iss con Inno Setup Compiler)

#define MyAppName      "Grunflex License Issuer"
#define MyAppVersion   "1.0.1"
#define MyAppPublisher "Grunflex (interno)"
#define MyIssuerExe    "Grunflex.LicenseIssuer.exe"
#define MyManagerExe   "GrunflexPOS.LicenseManager.exe"

[Setup]
AppId={{B7CA9F1E-2D6A-4D9F-9F2B-7E5B9A3FE901}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Grunflex\LicenseIssuer
DefaultGroupName=Grunflex (Interno)
DisableProgramGroupPage=no
DisableWelcomePage=yes
OutputDir=out
OutputBaseFilename=Grunflex_LicenseIssuer_Setup_{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayIcon={app}\{#MyIssuerExe}
ShowLanguageDialog=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear accesos directos en el escritorio"; GroupDescription: "Accesos directos:"

[Files]
Source: "staging\LicenseIssuer\*";  DestDir: "{app}\LicenseIssuer";  Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\LicenseManager\*"; DestDir: "{app}\LicenseManager"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "staging\Docs\*";           DestDir: "{app}\Docs";           Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Emitir licencias (LicenseIssuer)"; Filename: "{app}\LicenseIssuer\{#MyIssuerExe}"
Name: "{group}\Administrar licencia local (LicenseManager)"; Filename: "{app}\LicenseManager\{#MyManagerExe}"
Name: "{group}\Leeme (interno)"; Filename: "{app}\Docs\LEEME.txt"
Name: "{autodesktop}\Grunflex License Issuer"; Filename: "{app}\LicenseIssuer\{#MyIssuerExe}"; Tasks: desktopicon
Name: "{autodesktop}\Grunflex License Manager"; Filename: "{app}\LicenseManager\{#MyManagerExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\Docs\LEEME.txt"; Description: "Abrir notas internas"; Flags: postinstall shellexec skipifsilent unchecked

[Code]
procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpWelcome then
  begin
    WizardForm.WelcomeLabel2.Caption :=
      'Este instalador es solo para uso interno del editor.' + #13#10 +
      'No debe instalarse en equipos de clientes.' + #13#10#13#10 +
      'Instala el emisor de licencias (LicenseIssuer) y el administrador local (LicenseManager).';
  end;
end;
