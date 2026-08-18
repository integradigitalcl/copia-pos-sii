param(
  [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$installerDir = $PSScriptRoot

& (Join-Path $installerDir "build-staging.ps1") -Configuration $Configuration

$iscc = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { $iscc = Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe" }
if (-not (Test-Path $iscc)) { throw "Inno Setup 6 not found (ISCC.exe). Install from https://jrsoftware.org/isdl.php" }

$iss = Join-Path $installerDir "PosEdgeTerminal.iss"
$outDir = Join-Path $installerDir "out"
if (-not (Test-Path $outDir)) { $null = New-Item -ItemType Directory -Path $outDir -Force }

& $iscc $iss /O"$outDir"
Write-Host "Installer generated: $outDir"

