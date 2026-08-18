# Genera parche ejecutable de cambios visuales (UI Aero).
# Salida: ops/patch-ui/out/GrunflexVisualPatch.exe + payload/ + ZIP

$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$outDir = Join-Path $PSScriptRoot "out"
$payloadDir = Join-Path $outDir "payload"
$posProj = Join-Path $root "GrunflexPOS2\GrunflexPOS2.csproj"
$patchProj = Join-Path $root "tools\GrunflexVisualPatch\GrunflexVisualPatch.csproj"

Write-Host "==> Compilando Grunflex POS (Release)..." -ForegroundColor Cyan
dotnet build $posProj -c Release --no-incremental | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Fallo build POS" }

$posOut = Join-Path $root "GrunflexPOS2\bin\Release\net8.0-windows"
if (-not (Test-Path (Join-Path $posOut "GrunflexPOS2.exe"))) {
    throw "No se encontró binario Release en $posOut"
}

Write-Host "==> Preparando payload..." -ForegroundColor Cyan
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $payloadDir "Assets") -Force | Out-Null

Copy-Item (Join-Path $posOut "GrunflexPOS2.exe") $payloadDir -Force
Copy-Item (Join-Path $posOut "GrunflexPOS2.dll") $payloadDir -Force
Copy-Item (Join-Path $posOut "Assets\logo_login.png") (Join-Path $payloadDir "Assets") -Force

Write-Host "==> Publicando GrunflexVisualPatch.exe..." -ForegroundColor Cyan
dotnet publish $patchProj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $outDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Fallo publish parche" }

$readme = @"
Grunflex POS — Parche visual (UI Aero)
======================================

Contenido:
  GrunflexVisualPatch.exe   Ejecutable del parche (requiere admin)
  payload\                  Archivos a copiar a la instalación

Cambios incluidos:
  - Login: logo GrünFlex recortado con bordes redondos
  - Ventas: cabeceras legibles, búsqueda sin flecha duplicada
  - Dashboard: filtros de período alineados
  - Diálogos custom Aero (mensajes e inputs)
  - Tema ModernComboBoxFlat y estilos de grid

Uso:
  1. Cierre Grunflex POS (o deje que el parche lo cierre).
  2. Ejecute GrunflexVisualPatch.exe como administrador.
  3. Confirme la carpeta de instalación (Program Files\...\GrunflexPOS).
  4. Abra Grunflex POS.

Respaldo:
  Los archivos anteriores se guardan en %%TEMP%%\GrunflexVisualPatch-backup-<fecha>.

Generado: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
"@

Set-Content -Path (Join-Path $outDir "LEEME.txt") -Value $readme -Encoding UTF8

$zipPath = Join-Path $PSScriptRoot "GrunflexVisualPatch-UI.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $outDir "*") -DestinationPath $zipPath -Force

Write-Host ""
Write-Host "Listo:" -ForegroundColor Green
Write-Host "  $outDir\GrunflexVisualPatch.exe"
Write-Host "  $zipPath"
