# ==============================================================================
# Grunflex POS — Firewall multicaja (idempotente + verificación automática).
#
# -Role Server: reglas ENTRANTES (SMB 445, API 7279, discovery UDP 33279)
#               + reintento servicio GrunflexPOSAPI + probe health/local.
# -Role Client: reglas SALIENTES hacia principal (445, 7279, discovery).
#
# Uso manual (admin):
#   .\habilitar-firewall-multicaja.ps1 -Role Server
#   .\habilitar-firewall-multicaja.ps1 -Role Client -ServerIp 192.168.1.7
# ==============================================================================
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Server', 'Client')]
    [string] $Role,

    [string] $ServerIp = "",

    [switch] $SkipVerify
)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

function Run-Net([string] $argsLine) {
    $null = cmd /c "netsh $argsLine 2>&1"
    return ($LASTEXITCODE -eq 0)
}

function Enable-SharingFirewallGroups {
    foreach ($g in @('Compartir archivos e impresoras', 'File and Printer Sharing')) {
        try {
            $rules = Get-NetFirewallRule -DisplayGroup $g -ErrorAction SilentlyContinue |
                Where-Object { $_.Enabled -ne 'True' }
            if ($rules) {
                Set-NetFirewallRule -DisplayGroup $g -Enabled True -ErrorAction SilentlyContinue | Out-Null
                Write-Host "[Grunflex] Grupo firewall habilitado: $g"
            }
        }
        catch { }
    }
}

function Ensure-Rule([string] $name, [string] $netshAddLine) {
    $null = Run-Net "advfirewall firewall delete rule name=`"$name`""
    $ok = Run-Net $netshAddLine
    if (-not $ok) {
        Write-Host "[Grunflex] WARN: no se pudo crear regla '$name'" -ForegroundColor Yellow
    }
    return $ok
}

function Test-RulePresent([string] $name) {
    $out = cmd /c "netsh advfirewall firewall show rule name=`"$name`" 2>&1"
    return ($LASTEXITCODE -eq 0) -and ($out -match [regex]::Escape($name))
}

function Resolve-ServerIp {
    if (-not [string]::IsNullOrWhiteSpace($ServerIp)) {
        return $ServerIp.Trim().TrimEnd('/')
    }
    foreach ($p in @(
        "$env:ProgramData\PosEdge\config\configured-server-host.txt",
        "$env:ProgramData\PosEdge\config\install-server-host.txt"
    )) {
        if (Test-Path $p) {
            $v = (Get-Content $p -Raw -ErrorAction SilentlyContinue).Trim()
            if ($v -match '^https?://([^/:]+)') { return $Matches[1] }
            if ($v -match '^\d+\.\d+\.\d+\.\d+$') { return $v }
            if ($v) { return $v }
        }
    }
    $cfg = "$env:ProgramData\GrunflexPOS\config\appsettings.local.json"
    if (-not (Test-Path $cfg)) { $cfg = "$env:LOCALAPPDATA\GrunflexPOS\config\appsettings.local.json" }
    if (Test-Path $cfg) {
        try {
            $j = Get-Content $cfg -Raw | ConvertFrom-Json
            $u = [string]$j.Api.BaseUrl
            if ($u -match '//([^/:]+)') { return $Matches[1] }
        }
        catch { }
    }
    return ""
}

Write-Host "[Grunflex] Firewall multicaja ($Role)..."

Enable-SharingFirewallGroups

if ($Role -eq 'Client') {
    $null = Ensure-Rule 'Grunflex POS SMB (out 445)' `
        'advfirewall firewall add rule name="Grunflex POS SMB (out 445)" dir=out action=allow protocol=TCP remoteport=445 profile=any'
    $null = Ensure-Rule 'Grunflex POS API (out)' `
        'advfirewall firewall add rule name="Grunflex POS API (out)" dir=out action=allow protocol=TCP remoteport=7279 profile=any'
    $null = Ensure-Rule 'Grunflex POS Discovery out (33279)' `
        'advfirewall firewall add rule name="Grunflex POS Discovery out (33279)" dir=out action=allow protocol=UDP remoteport=33279 profile=any'
    $null = Ensure-Rule 'Grunflex POS Discovery in (33279)' `
        'advfirewall firewall add rule name="Grunflex POS Discovery in (33279)" dir=in action=allow protocol=UDP localport=33279 profile=any'
}
else {
    $null = Ensure-Rule 'Grunflex POS SMB (445 in)' `
        'advfirewall firewall add rule name="Grunflex POS SMB (445 in)" dir=in action=allow protocol=TCP localport=445 profile=any'
    $null = Ensure-Rule 'Grunflex POS API (7279)' `
        'advfirewall firewall add rule name="Grunflex POS API (7279)" dir=in action=allow protocol=TCP localport=7279 profile=any'
    $null = Ensure-Rule 'Grunflex POS Discovery (33279)' `
        'advfirewall firewall add rule name="Grunflex POS Discovery (33279)" dir=in action=allow protocol=UDP localport=33279 profile=any'

    $svc = 'GrunflexPOSAPI'
    try {
        $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
        if ($null -ne $s -and $s.Status -ne 'Running') {
            Start-Service -Name $svc -ErrorAction SilentlyContinue
            Write-Host "[Grunflex] Reintento de inicio del servicio $svc."
        }
    }
    catch { }
}

$exitCode = 0

if (-not $SkipVerify) {
    if ($Role -eq 'Server') {
        if (-not (Test-RulePresent 'Grunflex POS API (7279)')) {
            Write-Host "[Grunflex] FAIL: falta regla entrante TCP 7279" -ForegroundColor Red
            $exitCode = 1
        }
        else {
            Write-Host "[Grunflex] OK regla TCP 7279 (entrante)" -ForegroundColor Green
        }

        $listening = @(Get-NetTCPConnection -LocalPort 7279 -State Listen -ErrorAction SilentlyContinue).Count -gt 0
        if ($listening) {
            Write-Host "[Grunflex] OK puerto 7279 en escucha" -ForegroundColor Green
            try {
                Invoke-RestMethod 'http://127.0.0.1:7279/health/live' -TimeoutSec 5 | Out-Null
                Write-Host "[Grunflex] OK health/live local" -ForegroundColor Green
            }
            catch {
                Write-Host "[Grunflex] WARN: 7279 abierto pero health/live no respondió" -ForegroundColor Yellow
            }
        }
        else {
            Write-Host "[Grunflex] WARN: puerto 7279 no escucha (servicio API detenido?)" -ForegroundColor Yellow
        }
    }
    else {
        if (-not (Test-RulePresent 'Grunflex POS API (out)')) {
            Write-Host "[Grunflex] FAIL: falta regla saliente TCP 7279" -ForegroundColor Red
            $exitCode = 1
        }
        else {
            Write-Host "[Grunflex] OK regla TCP 7279 (saliente)" -ForegroundColor Green
        }

        $target = Resolve-ServerIp
        if ($target -and $target -notmatch '^(127\.|localhost)' ) {
            Write-Host "[Grunflex] Probando API en ${target}:7279 ..."
            try {
                Invoke-RestMethod "http://${target}:7279/health/live" -TimeoutSec 6 | Out-Null
                Write-Host "[Grunflex] OK health/live hacia $target" -ForegroundColor Green
            }
            catch {
                Write-Host "[Grunflex] WARN: no responde health/live en $target (firewall remoto, API apagada o IP incorrecta)" -ForegroundColor Yellow
            }
        }
    }
}

Write-Host "[Grunflex] Firewall multicaja ($Role) listo."
exit $exitCode
