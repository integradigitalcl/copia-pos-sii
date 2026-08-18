param(
  [string] $PayloadRoot
)

$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"

function Write-Step([string] $msg) { Write-Host $msg }
function Read-TextOrEmpty([string] $path) { try { if (Test-Path $path) { return (Get-Content -Raw -Path $path).Trim() } } catch { } return "" }

if ([string]::IsNullOrWhiteSpace($PayloadRoot)) { throw "PayloadRoot required." }

$cfgDir = Join-Path $env:ProgramData "PosEdge\\config"
$rolePath = Join-Path $cfgDir "role.txt"
$role = Read-TextOrEmpty $rolePath

if ($role -ne "server" -and $role -ne "terminal") {
  throw "Rol inválido."
}

if ($role -eq "server") {
  Write-Step "Preparando servidor..."
  & (Join-Path $PayloadRoot "SetupExtras\\posedgesetup-server.ps1") -PayloadRoot $PayloadRoot
  exit 0
}

Write-Step "Configurando terminal..."
$cli = Join-Path $PayloadRoot "DiscoveryCli\\PosEdge.DiscoveryCli.exe"
if (-not (Test-Path $cli)) { throw "DiscoveryCli missing." }

$api = & $cli "--prefer-udp" "--port" "5071" "--timeout-ms" "2500"
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($api)) {
  throw "No se encontró servidor multicaja en la red."
}

& (Join-Path $PayloadRoot "SetupExtras\\posedgesetup-terminal.ps1") -PayloadRoot $PayloadRoot -ApiBaseUrl $api
exit 0

