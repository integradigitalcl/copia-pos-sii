param(
  [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$singleDir = Join-Path $root "single"
$monoDir = Join-Path $root "monocaja"

Write-Host "=== Staging (dotnet publish) ===" -ForegroundColor Cyan
& (Join-Path $singleDir "build-staging.ps1") -Configuration $Configuration

$iscc = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { $iscc = Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe" }
if (-not (Test-Path $iscc)) { throw "Inno Setup 6 not found (ISCC.exe)" }

function Compile-Iss([string] $issPath, [string] $outDir, [string] $expectedExeName) {
  if (-not (Test-Path $outDir)) { $null = New-Item -ItemType Directory -Path $outDir -Force }
  Write-Host "`n=== ISCC: $(Split-Path $issPath -Leaf) ===" -ForegroundColor Cyan
  & $iscc $issPath "/O$outDir"
  if ($LASTEXITCODE -ne 0) { throw "ISCC failed for $issPath (exit $LASTEXITCODE)" }
  $exe = Join-Path $outDir $expectedExeName
  if (-not (Test-Path $exe)) { throw "Missing: $exe" }
  $len = (Get-Item $exe).Length
  if ($len -lt 400MB) { throw "$expectedExeName too small ($len bytes) - build interrupted?" }
  $hash = (Get-FileHash $exe -Algorithm SHA256).Hash
  [PSCustomObject]@{
    Name = $expectedExeName
    Path = $exe
    SizeMB = [math]::Round($len / 1MB, 2)
    Sha256 = $hash
  }
}

$multicaja = Compile-Iss `
  (Join-Path $singleDir "PosEdgeSetup.iss") `
  (Join-Path $singleDir "out") `
  "PosEdge-Setup.exe"

$mono = Compile-Iss `
  (Join-Path $monoDir "PosEdgeMonoSetup.iss") `
  (Join-Path $monoDir "out") `
  "GrunflexPOS-Mono-Setup.exe"

Write-Host "`n=== Resumen ===" -ForegroundColor Green
$multicaja | Format-List
$mono | Format-List

$manifest = Join-Path $root "INSTALLERS-$Configuration.txt"
@(
  "Built: $(Get-Date -Format o)"
  "Configuration: $Configuration"
  ""
  "MULTICAJA: $($multicaja.Path)"
  "  Size: $($multicaja.SizeMB) MB"
  "  SHA256: $($multicaja.Sha256)"
  ""
  "MONO CAJA: $($mono.Path)"
  "  Size: $($mono.SizeMB) MB"
  "  SHA256: $($mono.Sha256)"
) | Set-Content -Path $manifest -Encoding UTF8
Write-Host "Manifest: $manifest"
