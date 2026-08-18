$ErrorActionPreference = "Stop"

param(
  [Parameter(Mandatory = $true)]
  [string] $ServerIp,

  [int] $Port = 5071
)

$base = "http://$ServerIp`:$Port"

Write-Host "[CHECK] $base"

Write-Host ""
Write-Host "[1] Ping"
ping -n 2 $ServerIp | Out-Host

Write-Host ""
Write-Host "[2] TCP Port"
try {
  $r = Test-NetConnection -ComputerName $ServerIp -Port $Port -WarningAction SilentlyContinue
  $r | Select-Object ComputerName,RemotePort,TcpTestSucceeded | Format-Table | Out-Host
} catch {
  Write-Host "[WARN] Test-NetConnection no disponible: $($_.Exception.Message)"
}

Write-Host ""
Write-Host "[3] HTTP health/live"
try {
  (Invoke-WebRequest "$base/health/live" -UseBasicParsing -TimeoutSec 3).StatusCode | Out-Host
  Invoke-RestMethod "$base/health/live" -TimeoutSec 3 | ConvertTo-Json -Depth 5 | Out-Host
} catch {
  Write-Host "[ERR] live failed: $($_.Exception.Message)"
}

Write-Host ""
Write-Host "[4] HTTP health/ready"
try {
  (Invoke-WebRequest "$base/health/ready" -UseBasicParsing -TimeoutSec 3).StatusCode | Out-Host
  Invoke-RestMethod "$base/health/ready" -TimeoutSec 3 | ConvertTo-Json -Depth 5 | Out-Host
} catch {
  Write-Host "[ERR] ready failed: $($_.Exception.Message)"
}

