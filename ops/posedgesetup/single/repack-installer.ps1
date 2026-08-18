$ErrorActionPreference = "Stop"
$installerDir = $PSScriptRoot
$staging = Join-Path $installerDir "staging\SetupExtras"
$multicaja = Join-Path $installerDir "..\..\multicaja" | Resolve-Path
if (Test-Path $multicaja) {
  Get-ChildItem -Path $multicaja -Filter "*.ps1" | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $staging -Force
  }
}

$iscc = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { $iscc = Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe" }
if (-not (Test-Path $iscc)) { throw "Inno Setup 6 not found (ISCC.exe)" }

$outDir = Join-Path $installerDir "out"
& $iscc (Join-Path $installerDir "PosEdgeSetup.iss") "/O$outDir"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$setupExe = Join-Path $outDir "PosEdge-Setup.exe"
$len = (Get-Item $setupExe).Length
$hash = (Get-FileHash $setupExe -Algorithm SHA256).Hash
Write-Host "Installer generated: $setupExe"
Write-Host "Size: $len bytes"
Write-Host "SHA256: $hash"
