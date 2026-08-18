$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"

function Write-Step([string] $msg) { Write-Host $msg }

function Ensure-Dir([string] $path) { if (-not (Test-Path $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null } }

function Read-TextOrNull([string] $path) { try { if (Test-Path $path) { return (Get-Content -Raw -Path $path).Trim() } } catch { } return $null }
function Write-Text([string] $path, [string] $value) { Ensure-Dir (Split-Path $path -Parent); Set-Content -Path $path -Value $value -Encoding UTF8 }

function Is-HealthyServer([string] $apiBase) {
  try {
    $u = $apiBase.TrimEnd('/') + "/health/ready"
    $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 -Uri $u
    return ($r.StatusCode -ge 200 -and $r.StatusCode -lt 300)
  } catch { return $false }
}

function Discover-Server([string] $cliPath) {
  try {
    $apiBase = & $cliPath "--prefer-udp" "--port" "5071" "--timeout-ms" "2500"
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($apiBase)) {
      $apiBase = $apiBase.Trim()
      if (-not $apiBase.EndsWith("/")) { $apiBase += "/" }
      return $apiBase
    }
  } catch { }
  return $null
}

# ===== Paths in installed app =====
$installRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appRoot = Resolve-Path (Join-Path $installRoot "..") | Select-Object -ExpandProperty Path
$cfgDir = Join-Path $env:ProgramData "PosEdge\\config"
Ensure-Dir $cfgDir

$rolePath = Join-Path $cfgDir "role.txt"
$primaryLock = Join-Path $cfgDir "primary-server.lock"
$cachedServer = Join-Path $cfgDir "cached-server.txt"
$machineIdPath = Join-Path $cfgDir "machine-id.txt"

$cli = Join-Path $appRoot "DiscoveryCli\\PosEdge.DiscoveryCli.exe"
if (-not (Test-Path $cli)) { throw "DiscoveryCli missing." }

Write-Step "Validando sistema..."

# Machine-id: stable identity for future pairing and reconnect logic.
if (-not (Test-Path $machineIdPath)) {
  Write-Text $machineIdPath ([Guid]::NewGuid().ToString("D"))
}

# If we already decided a role on this machine, keep it (avoid flipping roles).
$existingRole = Read-TextOrNull $rolePath
if ($existingRole -eq "server") {
  Write-Step "Servidor ya instalado en esta PC."
  exit 0
}
if ($existingRole -eq "terminal") {
  Write-Step "Terminal ya instalada en esta PC."
  exit 0
}

# Prevent accidental split-brain: if we previously created primary lock, we must remain server.
if (Test-Path $primaryLock) {
  Write-Step "Servidor primario detectado localmente."
  Write-Text $rolePath "server"
  exit 0
}

# Try cached server first (survives IP changes with re-discovery later)
$cached = Read-TextOrNull $cachedServer
if ($cached -and (Is-HealthyServer $cached)) {
  Write-Step "Servidor encontrado."
  Write-Text $rolePath "terminal"
  Write-Text $cachedServer $cached
  exit 0
}

# Try discovery
$found = Discover-Server $cli
if ($found -and (Is-HealthyServer $found)) {
  Write-Step "Servidor encontrado."
  Write-Text $rolePath "terminal"
  Write-Text $cachedServer $found
  exit 0
}

# No server found => become server
Write-Step "Preparando servidor..."
Write-Text $rolePath "server"
Write-Text $primaryLock ("primary:" + [Guid]::NewGuid().ToString("D"))
exit 0

