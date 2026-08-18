param(
  [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\\..\\..")).Path
$staging  = Join-Path $PSScriptRoot "staging"

function Ensure-EmptyDir([string] $path) {
  if (-not (Test-Path $path)) {
    $null = New-Item -ItemType Directory -Path $path -Force
    return
  }

  # Avoid hard-delete of the whole staging folder (can fail if antivirus locks large EXEs).
  # Best-effort: clear children; if a file is locked, keep it and proceed.
  Get-ChildItem -Path $path -Force -ErrorAction SilentlyContinue | ForEach-Object {
    try { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction Stop }
    catch { Write-Warning "No se pudo borrar: $($_.FullName) (locked?). Se conserva." }
  }
}

$null = New-Item -ItemType Directory -Path (Join-Path $staging "Api") -Force
$null = New-Item -ItemType Directory -Path (Join-Path $staging "Workers") -Force
$null = New-Item -ItemType Directory -Path (Join-Path $staging "Prerequisites") -Force
$null = New-Item -ItemType Directory -Path (Join-Path $staging "ServerExtras") -Force

Ensure-EmptyDir (Join-Path $staging "Api")
Ensure-EmptyDir (Join-Path $staging "Workers")
Ensure-EmptyDir (Join-Path $staging "ServerExtras")

Write-Host "Publishing PosEdge.Api (self-contained win-x64)..."
dotnet publish (Join-Path $repoRoot "src\\PosEdge.Api\\PosEdge.Api.csproj") `
  -c $Configuration -r win-x64 --self-contained true `
  -o (Join-Path $staging "Api") | Out-Host

Write-Host "Publishing PosEdge.Workers (self-contained win-x64)..."
dotnet publish (Join-Path $repoRoot "src\\PosEdge.Workers\\PosEdge.Workers.csproj") `
  -c $Configuration -r win-x64 --self-contained true `
  -o (Join-Path $staging "Workers") | Out-Host

Write-Host "Copying ServerExtras..."
Copy-Item -Path (Join-Path $PSScriptRoot "ServerExtras\\*") -Destination (Join-Path $staging "ServerExtras") -Recurse -Force

Write-Host "Copying schema.sql + seed.sql into installer payload..."
$schemaSrc = Join-Path $repoRoot "db\\schema\\schema.sql"
$seedSrc   = Join-Path $repoRoot "db\\seeds\\seed.sql"
if (-not (Test-Path $schemaSrc)) { throw "schema.sql not found: $schemaSrc" }
if (-not (Test-Path $seedSrc)) { throw "seed.sql not found: $seedSrc" }
Copy-Item -Path $schemaSrc -Destination (Join-Path $staging "ServerExtras\\schema.sql") -Force
Copy-Item -Path $seedSrc   -Destination (Join-Path $staging "ServerExtras\\seed.sql") -Force

Write-Host "Downloading PostgreSQL installer (EDB) for offline setup packaging..."
$pgOut = Join-Path $staging "Prerequisites\\postgresql-windows-x64.exe"
# Pin to a known filename pattern; update version here when needed.
$pgUrl = "https://get.enterprisedb.com/postgresql/postgresql-17.9-1-windows-x64.exe"
if (Test-Path $pgOut) {
  $len = (Get-Item $pgOut).Length
  if ($len -gt 50000000) {
    Write-Host "PostgreSQL installer already present ($len bytes). Keeping existing file."
  } else {
    Write-Host "Existing PostgreSQL installer seems too small; re-downloading..."
    Remove-Item -Force $pgOut -ErrorAction SilentlyContinue
    Invoke-WebRequest -Uri $pgUrl -OutFile $pgOut -UseBasicParsing
  }
} else {
  try {
    Invoke-WebRequest -Uri $pgUrl -OutFile $pgOut -UseBasicParsing
  } catch {
    throw "Failed to download PostgreSQL installer from $pgUrl. Download it manually and place as: $pgOut"
  }
}
if (-not (Test-Path $pgOut) -or (Get-Item $pgOut).Length -lt 50000000) {
  throw "Downloaded PostgreSQL installer seems too small. Expected a full installer. File: $pgOut"
}

Write-Host "Done staging: $staging"

