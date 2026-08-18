# Publica POS y API en ops/installer/staging para compilar el instalador Inno Setup.
# Uso: .\build-staging.ps1  [-Configuration Release] [-PublicKeyPemPath <ruta>]
#
# Si no se especifica -PublicKeyPemPath, se busca automáticamente en:
#   %LocalAppData%\GrunflexPOS\data\licensing-public.pem
# El PEM encontrado se inserta en staging\POS\appsettings.json y staging\API\appsettings.json
# bajo "Licensing":"PublicKeyPem" para que el POS pueda validar las licencias firmadas
# por tu LicenseIssuer (clave privada nunca sale de tu PC).
param(
    [string] $Configuration = "Release",
    [string] $PublicKeyPemPath = $null
)
$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$Staging = Join-Path $PSScriptRoot "staging"

if (Test-Path $Staging) {
    Remove-Item $Staging -Recurse -Force
}
$null = New-Item -ItemType Directory -Path (Join-Path $Staging "POS")
$null = New-Item -ItemType Directory -Path (Join-Path $Staging "API")
$prereq = Join-Path $Staging "Prerequisites"
$null = New-Item -ItemType Directory -Path $prereq -Force

$webviewSetup = Join-Path $prereq "MicrosoftEdgeWebview2Setup.exe"
Write-Host "Descargando Microsoft Edge WebView2 Runtime (bootstrapper)..."
Invoke-WebRequest -Uri "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $webviewSetup -UseBasicParsing
if (-not (Test-Path $webviewSetup) -or (Get-Item $webviewSetup).Length -lt 100000) {
    throw "No se pudo descargar MicrosoftEdgeWebview2Setup.exe (WebView2). Revise la conexión a Internet."
}

Write-Host "Publicando POS (win-x64, autónomo)..."
& dotnet publish (Join-Path $RepoRoot "GrunflexPOS2\GrunflexPOS2.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -o (Join-Path $Staging "POS") | Write-Host

Write-Host "Publicando API (win-x64, autónomo)..."
& dotnet publish (Join-Path $RepoRoot "GrunflexPOS.API\GrunflexPOS.API.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -o (Join-Path $Staging "API") | Write-Host

Write-Host "Copiando extras servidor..."
$extrasSrc = Join-Path $PSScriptRoot "ServerExtras"
$extrasDst = Join-Path $Staging "ServerExtras"
$null = New-Item -ItemType Directory -Path $extrasDst -Force
Copy-Item -Path (Join-Path $extrasSrc "*") -Destination $extrasDst -Recurse -Force

# No incluir configuración local con credenciales en el instalador.
$localPos = Join-Path $Staging "POS\appsettings.local.json"
if (Test-Path $localPos) { Remove-Item $localPos -Force }
$localApi = Join-Path $Staging "API\appsettings.local.json"
if (Test-Path $localApi) { Remove-Item $localApi -Force }
$devApi = Join-Path $Staging "API\appsettings.Development.json"
if (Test-Path $devApi) { Remove-Item $devApi -Force }

# --- Inyectar clave pública RSA del editor en appsettings.json (POS y API) ---
if (-not $PublicKeyPemPath) {
    $PublicKeyPemPath = Join-Path $env:LOCALAPPDATA "GrunflexPOS\data\licensing-public.pem"
}

if (Test-Path $PublicKeyPemPath) {
    $pem = (Get-Content $PublicKeyPemPath -Raw).Trim()
    if (-not [string]::IsNullOrWhiteSpace($pem)) {
        # Empaquetamos el PEM como literal JSON (con \n y comillas escapadas).
        $jsonPem = ConvertTo-Json $pem -Compress

        $targets = @(
            (Join-Path $Staging "POS\appsettings.json"),
            (Join-Path $Staging "API\appsettings.json")
        )
        foreach ($tgt in $targets) {
            if (-not (Test-Path $tgt)) { continue }

            $contenido = Get-Content $tgt -Raw

            if ($contenido -match '"Licensing"\s*:\s*\{') {
                if ($contenido -match '"PublicKeyPem"\s*:\s*"[^"]*"') {
                    $contenido = [regex]::Replace(
                        $contenido,
                        '"PublicKeyPem"\s*:\s*"[^"]*"',
                        '"PublicKeyPem": ' + $jsonPem)
                } else {
                    $contenido = [regex]::Replace(
                        $contenido,
                        '("Licensing"\s*:\s*\{)',
                        '$1' + "`n    " + '"PublicKeyPem": ' + $jsonPem + ',')
                }
            } else {
                $licensingBloque = "`n  `"Licensing`": { `"PublicKeyPem`": $jsonPem },"
                $contenido = [regex]::Replace($contenido, '^\{', '{' + $licensingBloque)
            }

            Set-Content -Path $tgt -Value $contenido -Encoding UTF8
        }
        Write-Host "Clave publica RSA insertada en staging (POS y API)." -ForegroundColor Green
    }
} else {
    Write-Warning "No se encontro licensing-public.pem ($PublicKeyPemPath)."
    Write-Warning "El POS instalado en clientes NO podra validar tokens firmados por tu LicenseIssuer."
    Write-Warning "Genera primero una licencia con LicenseIssuer en esta PC, o pase -PublicKeyPemPath."
}

Write-Host "Listo: $Staging"
