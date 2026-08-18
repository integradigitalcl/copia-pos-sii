param(
  [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$installerDir = $PSScriptRoot
& (Join-Path $installerDir "build-staging.ps1") -Configuration $Configuration

$iscc = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { $iscc = Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe" }
if (-not (Test-Path $iscc)) { throw "Inno Setup 6 not found (ISCC.exe). Install from https://jrsoftware.org/isdl.php" }

$iss = Join-Path $installerDir "PosEdgeSetup.iss"
$outDir = Join-Path $installerDir "out"
if (-not (Test-Path $outDir)) { $null = New-Item -ItemType Directory -Path $outDir -Force }

& $iscc $iss /O"$outDir"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$setupExe = Join-Path $outDir "PosEdge-Setup.exe"
if (-not (Test-Path $setupExe)) { throw "Expected output not found: $setupExe" }

# Truncated builds (~30-40 MB) fail at launch with "setup files are corrupted".
$minBytes = 400MB
$len = (Get-Item $setupExe).Length
if ($len -lt $minBytes) {
  throw "PosEdge-Setup.exe is only $len bytes (expected >= $minBytes). Re-run compile without interrupting ISCC."
}

$hash = (Get-FileHash $setupExe -Algorithm SHA256).Hash
Write-Host "Installer generated: $setupExe"
Write-Host "Size: $len bytes ($([math]::Round($len / 1MB, 2)) MB)"
Write-Host "SHA256: $hash"

