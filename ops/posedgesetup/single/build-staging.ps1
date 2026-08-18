param(

  [string] $Configuration = "Release"

)



$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\\..\\..")).Path

$staging  = Join-Path $PSScriptRoot "staging"



function Ensure-EmptyDir([string] $path) {

  if (-not (Test-Path $path)) { $null = New-Item -ItemType Directory -Path $path -Force; return }

  Get-ChildItem -Path $path -Force -ErrorAction SilentlyContinue | ForEach-Object {

    try { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction Stop }

    catch { Write-Warning "No se pudo borrar: $($_.FullName) (locked?). Se conserva." }

  }

}



$dirs = @(

  "GrunflexPOS", "GrunflexApi", "Launcher", "Api", "Workers", "DiscoveryCli",

  "Bootstrapper", "SetupAgent", "Guardian", "DiagCli", "SelfCheckCli", "BackupCli",

  "Prerequisites", "SetupExtras", "DevTerminal"

)

$null = New-Item -ItemType Directory -Path $staging -Force

foreach ($d in $dirs) {

  $null = New-Item -ItemType Directory -Path (Join-Path $staging $d) -Force

  Ensure-EmptyDir (Join-Path $staging $d)

}



Write-Host "Publishing GrunflexPOS2 (WPF POS)..."

dotnet publish (Join-Path $repoRoot "GrunflexPOS2\\GrunflexPOS2.csproj") -c $Configuration -r win-x64 --self-contained true -o (Join-Path $staging "GrunflexPOS") | Out-Host



Write-Host "Publishing GrunflexPOS.API..."

dotnet publish (Join-Path $repoRoot "GrunflexPOS.API\\GrunflexPOS.API.csproj") -c $Configuration -r win-x64 --self-contained true -o (Join-Path $staging "GrunflexApi") | Out-Host



Write-Host "Publishing PosEdge.Launcher..."

dotnet publish (Join-Path $repoRoot "tools\\PosEdge.Launcher\\PosEdge.Launcher.csproj") -c $Configuration -r win-x64 --self-contained true -o (Join-Path $staging "Launcher") | Out-Host



Write-Host "Publishing PosEdge.Api (self-contained win-x64)..."

dotnet publish (Join-Path $repoRoot "src\\PosEdge.Api\\PosEdge.Api.csproj") -c $Configuration -r win-x64 --self-contained true -o (Join-Path $staging "Api") | Out-Host



Write-Host "Publishing PosEdge.Workers (self-contained win-x64)..."

dotnet publish (Join-Path $repoRoot "src\\PosEdge.Workers\\PosEdge.Workers.csproj") -c $Configuration -r win-x64 --self-contained true -o (Join-Path $staging "Workers") | Out-Host



Write-Host "Publishing PosEdge.Terminal (dev-only, hidden)..."

dotnet publish (Join-Path $repoRoot "src\\PosEdge.Terminal\\PosEdge.Terminal.csproj") -c $Configuration -r win-x64 --self-contained true -o (Join-Path $staging "DevTerminal") | Out-Host



Write-Host "Publishing PosEdge.DiscoveryCli..."

dotnet publish (Join-Path $repoRoot "tools\\PosEdge.DiscoveryCli\\PosEdge.DiscoveryCli.csproj") -c $Configuration -r win-x64 --self-contained true -o (Join-Path $staging "DiscoveryCli") | Out-Host



Write-Host "Publishing PosEdge.Bootstrapper..."

dotnet publish (Join-Path $repoRoot "tools\\PosEdge.Bootstrapper\\PosEdge.Bootstrapper.csproj") -c $Configuration -r win-x64 --self-contained true -o (Join-Path $staging "Bootstrapper") | Out-Host



Write-Host "Publishing PosEdge.SetupAgent..."

dotnet publish (Join-Path $repoRoot "tools\\PosEdge.SetupAgent\\PosEdge.SetupAgent.csproj") -c $Configuration -r win-x64 --self-contained true -o (Join-Path $staging "SetupAgent") | Out-Host



Write-Host "Publishing PosEdge.Guardian..."

dotnet publish (Join-Path $repoRoot "tools\\PosEdge.Guardian\\PosEdge.Guardian.csproj") -c $Configuration -r win-x64 --self-contained true -o (Join-Path $staging "Guardian") | Out-Host



Write-Host "Publishing PosEdge.DiagCli..."

dotnet publish (Join-Path $repoRoot "tools\\PosEdge.DiagCli\\PosEdge.DiagCli.csproj") -c $Configuration -r win-x64 --self-contained true -o (Join-Path $staging "DiagCli") | Out-Host



Write-Host "Publishing PosEdge.SelfCheckCli..."

dotnet publish (Join-Path $repoRoot "tools\\PosEdge.SelfCheckCli\\PosEdge.SelfCheckCli.csproj") -c $Configuration -r win-x64 --self-contained true -o (Join-Path $staging "SelfCheckCli") | Out-Host



Write-Host "Publishing PosEdge.BackupCli..."

dotnet publish (Join-Path $repoRoot "tools\\PosEdge.BackupCli\\PosEdge.BackupCli.csproj") -c $Configuration -r win-x64 --self-contained true -o (Join-Path $staging "BackupCli") | Out-Host



Write-Host "Copying schema.sql + seed.sql..."

Copy-Item -Path (Join-Path $repoRoot "db\\schema\\schema.sql") -Destination (Join-Path $staging "SetupExtras\\schema.sql") -Force

Copy-Item -Path (Join-Path $repoRoot "db\\seeds\\seed.sql") -Destination (Join-Path $staging "SetupExtras\\seed.sql") -Force



Write-Host "Copying setup extras..."

Copy-Item -Path (Join-Path $PSScriptRoot "SetupExtras\\*") -Destination (Join-Path $staging "SetupExtras") -Recurse -Force
Copy-Item -Path (Join-Path $repoRoot "ops\\installer\\habilitar-firewall-multicaja.ps1") -Destination (Join-Path $staging "SetupExtras") -Force

$multicajaOps = Join-Path $repoRoot "ops\\multicaja"
if (Test-Path $multicajaOps) {
  Get-ChildItem -Path $multicajaOps -Filter "*.ps1" | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $staging "SetupExtras") -Force
  }
}



Write-Host "Downloading PostgreSQL installer..."

$pgOut = Join-Path $staging "Prerequisites\\postgresql-windows-x64.exe"

$pgUrl = "https://get.enterprisedb.com/postgresql/postgresql-17.9-1-windows-x64.exe"

if (-not (Test-Path $pgOut) -or (Get-Item $pgOut).Length -lt 50000000) {

  Invoke-WebRequest -Uri $pgUrl -OutFile $pgOut -UseBasicParsing

}

if ((Get-Item $pgOut).Length -lt 50000000) { throw "PostgreSQL payload download looks invalid: $pgOut" }



Write-Host "Downloading WebView2 bootstrapper..."

$wvOut = Join-Path $staging "Prerequisites\\MicrosoftEdgeWebview2Setup.exe"

if (-not (Test-Path $wvOut) -or (Get-Item $wvOut).Length -lt 100000) {

  Invoke-WebRequest -Uri "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $wvOut -UseBasicParsing

}



Write-Host "Done staging: $staging"


