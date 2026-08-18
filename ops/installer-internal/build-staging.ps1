# Compila el "Grunflex License Issuer" para uso interno (NO se vende, NO se distribuye).
# Incluye Grunflex.LicenseIssuer y GrunflexPOS.LicenseManager como herramientas administrativas.
# Uso: .\build-staging.ps1  [-Configuration Release]
param(
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$Staging  = Join-Path $PSScriptRoot "staging"

if (Test-Path $Staging) {
    Remove-Item $Staging -Recurse -Force
}
$null = New-Item -ItemType Directory -Path (Join-Path $Staging "LicenseIssuer")
$null = New-Item -ItemType Directory -Path (Join-Path $Staging "LicenseManager")
$null = New-Item -ItemType Directory -Path (Join-Path $Staging "Docs")

Write-Host "Publicando Grunflex.LicenseIssuer (win-x64, autónomo)..."
$issuerProj = Join-Path $RepoRoot "Grunflex.LicenseIssuer\Grunflex.LicenseIssuer.csproj"
if (-not (Test-Path $issuerProj)) {
    throw "No se encuentra el proyecto Grunflex.LicenseIssuer en: $issuerProj"
}
& dotnet publish $issuerProj `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -o (Join-Path $Staging "LicenseIssuer") | Write-Host

Write-Host "Publicando GrunflexPOS.LicenseManager (win-x64, autónomo)..."
$managerProj = Join-Path $RepoRoot "GrunflexPOS.LicenseManager\GrunflexPOS.LicenseManager.csproj"
if (Test-Path $managerProj) {
    & dotnet publish $managerProj `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -o (Join-Path $Staging "LicenseManager") | Write-Host
} else {
    Write-Warning "GrunflexPOS.LicenseManager.csproj no encontrado, se omite del paquete interno."
}

# No incluir configuración local con secretos en el ejecutable distribuido (aunque sea interno).
$localIssuer = Join-Path $Staging "LicenseIssuer\appsettings.local.json"
if (Test-Path $localIssuer) { Remove-Item $localIssuer -Force }
$localManager = Join-Path $Staging "LicenseManager\appsettings.local.json"
if (Test-Path $localManager) { Remove-Item $localManager -Force }

# Documentación interna.
$readme = @"
GRUNFLEX – HERRAMIENTAS INTERNAS DE LICENCIAS
=============================================

Este paquete contiene utilidades EXCLUSIVAS para uso interno del editor.
NO debe entregarse ni instalarse en equipos de clientes.

Incluye:
- Grunflex.LicenseIssuer.exe  → firma y emite tokens de licencia (.lic) con clave privada RSA.
- GrunflexPOS.LicenseManager.exe → aplica o reemplaza la licencia almacenada en una caja concreta.

Flujo recomendado:
1. Generar la licencia con LicenseIssuer (la firma queda en %LocalAppData%\GrunflexPOS\data\api.secrets.json).
2. Enviar el archivo .lic al cliente o entregarlo en mano.
3. El cliente lo pega en la pantalla de Activación del POS (no requiere LicenseIssuer ni LicenseManager).

La carpeta de datos sensibles (claves RSA, IssuerApiKey) está en:
  %LocalAppData%\GrunflexPOS\data\api.secrets.json
HACER COPIAS DE SEGURIDAD CIFRADAS DE ESE ARCHIVO.
"@
Set-Content -Path (Join-Path $Staging "Docs\LEEME.txt") -Value $readme -Encoding UTF8

Write-Host "Listo: $Staging"
