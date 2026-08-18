param(
  [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\\..\\..")).Path
$staging  = Join-Path $PSScriptRoot "staging"

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

$null = New-Item -ItemType Directory -Path (Join-Path $staging "Terminal") -Force
$null = New-Item -ItemType Directory -Path (Join-Path $staging "DiscoveryCli") -Force
$null = New-Item -ItemType Directory -Path (Join-Path $staging "TerminalExtras") -Force

Write-Host "Publishing PosEdge.Terminal (self-contained win-x64)..."
dotnet publish (Join-Path $repoRoot "src\\PosEdge.Terminal\\PosEdge.Terminal.csproj") `
  -c $Configuration -r win-x64 --self-contained true `
  -o (Join-Path $staging "Terminal") | Out-Host

Write-Host "Publishing PosEdge.DiscoveryCli (self-contained win-x64)..."
dotnet publish (Join-Path $repoRoot "tools\\PosEdge.DiscoveryCli\\PosEdge.DiscoveryCli.csproj") `
  -c $Configuration -r win-x64 --self-contained true `
  -o (Join-Path $staging "DiscoveryCli") | Out-Host

Write-Host "Copying TerminalExtras..."
Copy-Item -Path (Join-Path $PSScriptRoot "TerminalExtras\\*") -Destination (Join-Path $staging "TerminalExtras") -Recurse -Force

Write-Host "Done staging: $staging"

