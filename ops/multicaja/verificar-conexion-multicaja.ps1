# Verifica conexion multicaja desde caja principal o adicional.
param(
    [string] $ServerIp = "",
    [switch] $RepararFirewall
)

$ErrorActionPreference = "Continue"
$hostName = $env:COMPUTERNAME
$roleFile = "$env:ProgramData\PosEdge\config\role.txt"
$posEdgeRole = if (Test-Path $roleFile) { (Get-Content $roleFile -Raw).Trim() } else { "(sin role.txt)" }

Write-Host "=== Equipo: $hostName | PosEdge: $posEdgeRole ===" -ForegroundColor Cyan

$cfgPath = "$env:ProgramData\GrunflexPOS\config\appsettings.local.json"
if (-not (Test-Path $cfgPath)) { $cfgPath = "$env:LOCALAPPDATA\GrunflexPOS\config\appsettings.local.json" }
if (Test-Path $cfgPath) {
    $j = Get-Content $cfgPath -Raw | ConvertFrom-Json
    Write-Host "TerminalRole: $($j.TerminalRole)"
    Write-Host "Api: $($j.Api.BaseUrl)"
    Write-Host "UseApiOnlyClient: $($j.Multicaja.UseApiOnlyClient)"
    $apiLocal = ($j.Api.BaseUrl -like '*localhost*') -or ($j.Api.BaseUrl -like '*127.0.0.1*')
    if ($apiLocal -and ($j.TerminalRole -eq 'client')) {
        Write-Host "PROBLEMA: caja adicional con API en localhost (debe ser IP de la principal)" -ForegroundColor Red
    }
}

if ([string]::IsNullOrWhiteSpace($ServerIp)) {
    if ($posEdgeRole -eq 'server' -or $j.TerminalRole -eq 'server') {
        $ServerIp = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object {
            $_.IPAddress -notlike '127.*' -and $_.PrefixOrigin -ne 'WellKnown'
        } | Select-Object -First 1).IPAddress
        Write-Host "`nIP sugerida de este servidor: $ServerIp"
    } else {
        $u = $j.Api.BaseUrl
        if ($u -match '//([^/:]+)') { $ServerIp = $Matches[1] }
    }
}

if ([string]::IsNullOrWhiteSpace($ServerIp)) {
    Write-Host "Indique -ServerIp con la IP de la caja principal."
    exit 2
}

$base = "http://${ServerIp}:7279/"
Write-Host "`n=== Probando $base ==="
$failCount = 0
foreach ($ep in @('health/live', 'health/ready', 'api/multicaja/capabilities', 'api/terminals')) {
    try {
        $r = Invoke-WebRequest ($base + $ep) -UseBasicParsing -TimeoutSec 6
        Write-Host "[OK] $ep -> $($r.StatusCode)" -ForegroundColor Green
    } catch {
        Write-Host "[FAIL] $ep -> $($_.Exception.Message)" -ForegroundColor Red
        $failCount++
    }
}

if ($failCount -gt 0 -and $RepararFirewall) {
    Write-Host "`n=== Reparando firewall (7279) ===" -ForegroundColor Yellow
    $repair = Join-Path $PSScriptRoot 'reparar-firewall-multicaja.ps1'
    if (Test-Path $repair) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $repair -ServerIp $ServerIp
        Write-Host "`n=== Reintento tras firewall ==="
        $failCount = 0
        foreach ($ep in @('health/live', 'health/ready')) {
            try {
                $r = Invoke-WebRequest ($base + $ep) -UseBasicParsing -TimeoutSec 6
                Write-Host "[OK] $ep -> $($r.StatusCode)" -ForegroundColor Green
            } catch {
                Write-Host "[FAIL] $ep -> $($_.Exception.Message)" -ForegroundColor Red
                $failCount++
            }
        }
    }
}

if ($failCount -gt 0) { exit 3 }

try {
    $terms = Invoke-RestMethod ($base + 'api/terminals') -TimeoutSec 8
    Write-Host "`nTerminales en servidor ($ServerIp): $($terms.Count)"
    foreach ($t in $terms) {
        Write-Host "  - $($t.machineName) activo=$($t.active) ultimo=$($t.lastHeartbeatUtc)"
    }
    if ($terms.Count -eq 0) {
        Write-Host "Ninguna terminal registrada: la caja adicional NO ha hecho register/heartbeat aun." -ForegroundColor Yellow
    }
} catch {
    Write-Host "No se pudo listar terminales: $($_.Exception.Message)" -ForegroundColor Red
}
