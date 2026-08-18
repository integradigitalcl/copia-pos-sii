param(
  [string] $PayloadRoot,
  [string] $ApiBaseUrl
)

$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"

function Write-Step([string] $msg) { Write-Host $msg }
function Ensure-Dir([string] $path) { if (-not (Test-Path $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null } }

function Write-TerminalConfig([string] $targetPath, [string] $apiBaseUrl) {
  $obj = @{
    Terminal = @{
      TenantId = "11111111-1111-1111-1111-111111111111"
      BranchId = "22222222-2222-2222-2222-222222222222"
      TerminalId = ""
      CashSessionId = ""
      OfflineMode = "LeasesOnly"
      ReplicaPath = ""
    }
    Api = @{ BaseUrl = $apiBaseUrl }
  }
  Ensure-Dir (Split-Path $targetPath -Parent)
  ($obj | ConvertTo-Json -Depth 5) | Set-Content -Path $targetPath -Encoding UTF8
}

function Register-Autostart([string] $exePath) {
  $key = "HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"
  $cmd = "cmd.exe /c start `"`"PosEdge Terminal`"`" /min `"`"$exePath`"`""
  New-ItemProperty -Path $key -Name "PosEdgeTerminal" -Value $cmd -PropertyType String -Force | Out-Null
}

function Cache-Server([string] $apiBaseUrl) {
  $cfgDir = Join-Path $env:ProgramData "PosEdge\\config"
  Ensure-Dir $cfgDir
  Set-Content -Path (Join-Path $cfgDir "cached-server.txt") -Value $apiBaseUrl -Encoding UTF8
  Set-Content -Path (Join-Path $cfgDir "role.txt") -Value "terminal" -Encoding UTF8
}

function Test-Live([string] $apiBaseUrl) {
  try {
    $u = $apiBaseUrl.TrimEnd('/') + "/health/live"
    $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 -Uri $u
    return ($r.StatusCode -ge 200 -and $r.StatusCode -lt 300)
  } catch { return $false }
}

if ([string]::IsNullOrWhiteSpace($PayloadRoot)) { throw "PayloadRoot required." }
if ([string]::IsNullOrWhiteSpace($ApiBaseUrl)) { throw "ApiBaseUrl required." }

$api = $ApiBaseUrl.Trim()
if (-not $api.EndsWith("/")) { $api += "/" }

Write-Step "Configurando terminal..."
if (-not (Test-Live $api)) { throw "No se pudo validar conexión al servidor." }

$terminalDir = Join-Path $PayloadRoot "Terminal"
Write-TerminalConfig -targetPath (Join-Path $terminalDir "appsettings.Production.json") -apiBaseUrl $api
Register-Autostart -exePath (Join-Path $terminalDir "PosEdge.Terminal.exe")
Cache-Server $api

Write-Step "Terminal multicaja lista."

