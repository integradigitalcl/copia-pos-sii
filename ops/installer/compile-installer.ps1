# Ejecuta build-staging.ps1 y luego ISCC.exe (Inno Setup 6).
$ErrorActionPreference = "Stop"
$installerDir = $PSScriptRoot

& (Join-Path $installerDir "build-staging.ps1")

$iscc = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    $iscc = Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"
}
if (-not (Test-Path $iscc)) {
    throw "No se encontró Inno Setup 6 (ISCC.exe). Instálelo desde https://jrsoftware.org/isdl.php"
}

$iss = Join-Path $installerDir "GrunflexPOS.iss"
$outDir = Join-Path $installerDir "out"
if (-not (Test-Path $outDir)) {
    $null = New-Item -ItemType Directory -Path $outDir
}

& $iscc $iss /O"$outDir"
Write-Host "Instalador generado en: $outDir"
