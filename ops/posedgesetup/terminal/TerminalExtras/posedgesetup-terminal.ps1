$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"

function Write-Step([string] $msg) { Write-Host $msg }

function Write-TerminalConfig([string] $targetPath, [string] $apiBaseUrl) {
  $json = @"
{
  "Terminal": {
    "TenantId": "11111111-1111-1111-1111-111111111111",
    "BranchId": "22222222-2222-2222-2222-222222222222",
    "TerminalId": "",
    "CashSessionId": "",
    "OfflineMode": "LeasesOnly",
    "ReplicaPath": ""
  },
  "Api": {
    "BaseUrl": "$apiBaseUrl"
  }
}
"@
  Set-Content -Path $targetPath -Value $json -Encoding UTF8
}

function Add-FirewallRules {
  # Client typically only needs outbound, but we keep it minimal (no inbound).
  Write-Step "Firewall OK."
}

function Register-Autostart([string] $name, [string] $exePath) {
  $key = "HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"
  # Start minimized (console) to avoid a technical UX.
  $cmd = "cmd.exe /c start `"`"PosEdge Terminal`"`" /min `"`"$exePath`"`""
  New-ItemProperty -Path $key -Name $name -Value $cmd -PropertyType String -Force | Out-Null
}

Write-Step "=== PosEdge Terminal Setup ==="
$installRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appRoot = Resolve-Path (Join-Path $installRoot "..") | Select-Object -ExpandProperty Path

$cli = Join-Path $appRoot "DiscoveryCli\\PosEdge.DiscoveryCli.exe"
if (-not (Test-Path $cli)) { throw "DiscoveryCli missing." }

Write-Step "Buscando servidor en la red..."
$apiBase = & $cli "--prefer-udp" "--port" "5071"
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($apiBase)) {
  throw "No se encontró servidor multicaja en la red."
}
$apiBase = $apiBase.Trim()
if (-not $apiBase.EndsWith("/")) { $apiBase = $apiBase + "/" }

Write-Step "Configurando terminal..."
Write-TerminalConfig -targetPath (Join-Path $appRoot "Terminal\\appsettings.Production.json") -apiBaseUrl $apiBase

Add-FirewallRules

Write-Step "Configurando inicio automático..."
Register-Autostart -name "PosEdgeTerminal" -exePath (Join-Path $appRoot "Terminal\\PosEdge.Terminal.exe")

Write-Step "Validando conexión..."
try {
  $live = Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 -Uri ($apiBase + "health/live")
  if ($live.StatusCode -lt 200 -or $live.StatusCode -ge 300) { throw "live" }
} catch { throw "No se pudo validar conexión al servidor." }

Write-Step "OK: Terminal multicaja lista."

