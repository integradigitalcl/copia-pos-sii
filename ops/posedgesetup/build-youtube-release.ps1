# Parche YouTube + recompilación de instaladores multicaja y monocaja.
# Uso: .\build-youtube-release.ps1 [-Configuration Release] [-SkipInstallers]
#
# Salidas:
#   ops/patch-youtube/out/GrunflexYouTubePatch.exe  — hotfix para PCs ya instalados
#   ops/posedgesetup/single/out/PosEdge-Setup.exe
#   ops/posedgesetup/monocaja/out/GrunflexPOS-Mono-Setup.exe

param(
    [string] $Configuration = "Release",
    [switch] $SkipInstallers
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$posProj = Join-Path $repoRoot "GrunflexPOS2\GrunflexPOS2.csproj"
$patchProj = Join-Path $repoRoot "tools\GrunflexVisualPatch\GrunflexVisualPatch.csproj"
$patchOut = Join-Path $repoRoot "ops\patch-youtube\out"
$payloadDir = Join-Path $patchOut "payload"

Write-Host "=== 1/3 Compilar Grunflex POS ($Configuration) ===" -ForegroundColor Cyan
dotnet build $posProj -c $Configuration --no-incremental | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Fallo build POS" }

$posOut = Join-Path $repoRoot "GrunflexPOS2\bin\$Configuration\net8.0-windows"
if (-not (Test-Path (Join-Path $posOut "GrunflexPOS2.exe"))) {
    throw "No se encontró GrunflexPOS2.exe en $posOut"
}

Write-Host "=== 2/3 Generar parche hotfix (instalaciones existentes) ===" -ForegroundColor Cyan
if (Test-Path $patchOut) { Remove-Item $patchOut -Recurse -Force }
New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null

Copy-Item (Join-Path $posOut "GrunflexPOS2.exe") $payloadDir -Force
Copy-Item (Join-Path $posOut "GrunflexPOS2.dll") $payloadDir -Force

dotnet publish $patchProj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $patchOut | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Fallo publish parche" }

$hotfixExe = Join-Path $patchOut "GrunflexVisualPatch.exe"
$youtubeExe = Join-Path $patchOut "GrunflexYouTubePatch.exe"
if (Test-Path $hotfixExe) {
    Move-Item $hotfixExe $youtubeExe -Force
}

$readme = @"
Grunflex POS — Parche YouTube Music
===================================

Cambios:
  - Spotify eliminado del sidebar y Configuración
  - Widget YouTube Music en panel izquierdo (reproductor embebido)
  - Módulo Configuración → YouTube Music (navegador integrado)
  - Sin API keys ni cuenta de desarrollador

Uso del parche (PC ya instalado — multicaja o monocaja):
  1. Cierre Grunflex POS.
  2. Ejecute GrunflexYouTubePatch.exe como administrador.
  3. Confirme la carpeta GrunflexPOS (Program Files o PosEdge\GrunflexPOS).
  4. Abra Grunflex POS.

Instaladores nuevos:
  Ejecute este script sin -SkipInstallers o compile-all-installers.ps1

Generado: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
"@
Set-Content -Path (Join-Path $patchOut "LEEME.txt") -Value $readme -Encoding UTF8

$zipPath = Join-Path $repoRoot "ops\patch-youtube\GrunflexYouTubePatch.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $patchOut "*") -DestinationPath $zipPath -Force

Write-Host "Parche listo: $youtubeExe" -ForegroundColor Green
Write-Host "ZIP: $zipPath" -ForegroundColor Green

if ($SkipInstallers) {
    Write-Host "Omitiendo instaladores (-SkipInstallers)." -ForegroundColor Yellow
    exit 0
}

Write-Host "=== 3/3 Recompilar instaladores multicaja + monocaja ===" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "compile-all-installers.ps1") -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "Fallo compilación instaladores" }

Write-Host ""
Write-Host "=== Completado ===" -ForegroundColor Green
Write-Host "Hotfix:  $youtubeExe"
Write-Host "Multicaja: $(Join-Path $PSScriptRoot 'single\out\PosEdge-Setup.exe')"
Write-Host "Monocaja:  $(Join-Path $PSScriptRoot 'monocaja\out\GrunflexPOS-Mono-Setup.exe')"
