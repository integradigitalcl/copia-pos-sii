param(
  [string] $Configuration = "Release",
  [switch] $SkipStaging
)

$ErrorActionPreference = "Stop"
$monoDir = $PSScriptRoot
$singleDir = Join-Path $monoDir "..\single"

if (-not $SkipStaging) {
  & (Join-Path $singleDir "build-staging.ps1") -Configuration $Configuration
}

$iscc = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { $iscc = Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe" }
if (-not (Test-Path $iscc)) { throw "Inno Setup 6 not found (ISCC.exe)" }

$iss = Join-Path $monoDir "PosEdgeMonoSetup.iss"
$outDir = Join-Path $monoDir "out"
if (-not (Test-Path $outDir)) { $null = New-Item -ItemType Directory -Path $outDir -Force }

& $iscc $iss "/O$outDir"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$setupExe = Join-Path $outDir "GrunflexPOS-Mono-Setup.exe"
if (-not (Test-Path $setupExe)) { throw "Expected output not found: $setupExe" }

$minBytes = 400MB
$len = (Get-Item $setupExe).Length
if ($len -lt $minBytes) {
  throw "GrunflexPOS-Mono-Setup.exe is only $len bytes (expected >= $minBytes)."
}

$hash = (Get-FileHash $setupExe -Algorithm SHA256).Hash
Write-Host "Installer generated: $setupExe"
Write-Host "Size: $len bytes ($([math]::Round($len / 1MB, 2)) MB)"
Write-Host "SHA256: $hash"
