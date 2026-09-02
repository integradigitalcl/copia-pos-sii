param(
    [string]$StagingPath = "",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$staging = if ($StagingPath) { $StagingPath } else { Join-Path $root "artifacts/macos-installer" }
$outDir = if ($OutputDir) { $OutputDir } else {
    Join-Path ([Environment]::GetFolderPath("Desktop")) "GrunflexPOS-Web-Instalador"
}
$appName = "Grunflex POS Web.app"
$appPath = Join-Path $staging $appName
$zipOut = Join-Path $PSScriptRoot "out\GrunflexPOS-Web-macOS-Catalina.zip"
$packName = "GrunflexPOS-Web-macOS-Catalina"
$packDir = Join-Path $staging $packName
$isoPath = Join-Path $outDir "$packName.iso"
$zipDesk = Join-Path $outDir "$packName.zip"
$dmgPath = Join-Path $outDir "$packName.dmg"
$scripts = Join-Path $PSScriptRoot "macos"

if (-not (Test-Path $appPath)) {
    Write-Host "App bundle no encontrado; compilando paquete macOS..."
    & (Join-Path $PSScriptRoot "compile-macos-installer.ps1")
}
if (-not (Test-Path $appPath)) {
    throw "No se encontró '$appPath'."
}

function Write-UnixFile([string]$src, [string]$dst) {
    $t = [System.IO.File]::ReadAllText($src) -replace "`r`n", "`n" -replace "`r", "`n"
    [System.IO.File]::WriteAllText($dst, $t, (New-Object System.Text.UTF8Encoding $false))
}

# Carpeta de instalacion (ZIP/ISO) — compatible Catalina sin DMG Go.
if (Test-Path $packDir) { Remove-Item $packDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $packDir | Out-Null
Copy-Item -LiteralPath $appPath -Destination (Join-Path $packDir $appName) -Recurse -Force
Write-UnixFile (Join-Path $scripts "Instalar en Aplicaciones.command") (Join-Path $packDir "Instalar en Aplicaciones.command")
$readme = @"
Grunflex POS Web — macOS Catalina

1) Instale Firefox ESR: https://www.mozilla.org/firefox/enterprise/
2) Doble clic en "Instalar en Aplicaciones.command"
   (si macOS bloquea: clic derecho → Abrir)
3) Arrastre tambien "Grunflex POS Web.app" a Aplicaciones si prefiere

No use el .dmg generado en Windows si el Mac dice "imagen no reconocida":
use este ZIP o el .iso.
"@
[System.IO.File]::WriteAllText((Join-Path $packDir "LEEME.txt"), ($readme -replace "`r`n", "`n"), (New-Object System.Text.UTF8Encoding $false))

New-Item -ItemType Directory -Force -Path $outDir, (Join-Path $PSScriptRoot "out") | Out-Null

# ZIP principal (siempre funciona en Catalina)
foreach ($z in @($zipOut, $zipDesk)) {
    if (Test-Path $z) { Remove-Item $z -Force }
}
Compress-Archive -Path (Join-Path $packDir "*") -DestinationPath $zipOut -CompressionLevel Optimal
Copy-Item $zipOut $zipDesk -Force
Write-Host "ZIP listo: $zipDesk" -ForegroundColor Green

# ISO (Finder en Catalina suele abrirlo; el DMG puro-Go a menudo falla)
function New-MacInstallerIso([string]$SourceDir, [string]$IsoPath, [string]$VolumeName) {
    if (Test-Path $IsoPath) { Remove-Item $IsoPath -Force }
    $oscdimg = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe",
        "${env:ProgramFiles}\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $oscdimg) {
        throw "oscdimg.exe no disponible (Windows ADK)."
    }
    $label = ($VolumeName -replace '[^A-Za-z0-9_]', '').Substring(0, [Math]::Min(16, ($VolumeName -replace '[^A-Za-z0-9_]', '').Length))
    if ([string]::IsNullOrWhiteSpace($label)) { $label = "GRUNFLEXPOS" }
    & $oscdimg -m -o -u2 -l$label $SourceDir $IsoPath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $IsoPath) -or (Get-Item $IsoPath).Length -lt 1MB) {
        throw "oscdimg fallo (exit=$LASTEXITCODE)."
    }
}

try {
    New-MacInstallerIso -SourceDir $packDir -IsoPath $isoPath -VolumeName "GrunflexPOSWeb"
    Write-Host "ISO listo: $isoPath" -ForegroundColor Green
    Write-Host "En el Mac: abra el .iso (o el .zip) y ejecute 'Instalar en Aplicaciones.command'."
}
catch {
    Write-Warning "No se pudo crear ISO ($($_.Exception.Message)). Use el ZIP."
}

# DMG puro-Go: desactivado por defecto (Catalina suele decir "imagen no reconocida").
# Para forzarlo: $env:GRUNFLEX_BUILD_DMG = "1"
if ($env:GRUNFLEX_BUILD_DMG -eq "1") {
    $go = Get-Command go -ErrorAction SilentlyContinue
    if (-not $go -and (Test-Path "C:\Program Files\Go\bin\go.exe")) {
        $env:Path = "C:\Program Files\Go\bin;$env:USERPROFILE\go\bin;" + $env:Path
        $go = Get-Command go -ErrorAction SilentlyContinue
    }
    if ($go) {
        $mkdragDir = Join-Path $PSScriptRoot "macos\mkdragdmg"
        if (Test-Path $dmgPath) { Remove-Item $dmgPath -Force }
        Push-Location $mkdragDir
        try {
            Write-Host "Generando DMG experimental..."
            go run . $appPath $dmgPath 2>&1 | Out-Host
        }
        catch {
            Write-Warning "DMG no generado: $($_.Exception.Message)"
        }
        finally {
            Pop-Location
        }
    }
}
else {
    if (Test-Path $dmgPath) { Remove-Item $dmgPath -Force -ErrorAction SilentlyContinue }
    Write-Host "DMG omitido (use el ZIP). Para generar DMG experimental: `$env:GRUNFLEX_BUILD_DMG=1`"
}

Get-ChildItem $outDir -Filter "GrunflexPOS-Web-macOS-Catalina*" | Format-Table Name, Length, LastWriteTime
Write-Host "En el Mac: descomprima el ZIP y ejecute 'Instalar en Aplicaciones.command'." -ForegroundColor Yellow
