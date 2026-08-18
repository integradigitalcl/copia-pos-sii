$ErrorActionPreference = "Continue"
$ProgressPreference    = "SilentlyContinue"
$ConfirmPreference     = "None"

# Detiene y elimina el servicio Windows GrunflexPOSAPI.
# Se invoca desde el desinstalador (Inno [UninstallRun]). NO borra datos.

$serviceName = "GrunflexPOSAPI"
$logPath     = Join-Path $env:LOCALAPPDATA "GrunflexPOS\logs\setup-multicaja.log"
try {
    $logDir = Split-Path $logPath -Parent
    if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
} catch {}

function Log([string]$m) {
    Write-Host $m
    try { "$(Get-Date -Format s) [uninstall] $m" | Out-File -FilePath $logPath -Append -Encoding utf8 } catch {}
}

Log "Deteniendo y eliminando servicio $serviceName..."

$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -eq $svc) {
    Log "  Servicio no estaba instalado."
    exit 0
}

try {
    if ($svc.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
    }
} catch {
    Log "  [WARN] Stop: $($_.Exception.Message)"
}

try {
    cmd /c "sc.exe delete $serviceName 2>&1" | Out-Null
    Log "  Servicio eliminado."
} catch {
    Log "  [WARN] Delete: $($_.Exception.Message)"
}

# Tambien removemos las reglas de firewall para no dejar puertos abiertos huerfanos
try {
    cmd /c 'netsh advfirewall firewall delete rule name="Grunflex POS API (7279)" 2>&1' | Out-Null
    cmd /c 'netsh advfirewall firewall delete rule name="Grunflex POS API (out)" 2>&1' | Out-Null
    cmd /c 'netsh advfirewall firewall delete rule name="Grunflex POS Discovery (33279)" 2>&1' | Out-Null
} catch {}

# El share SMB se deja a discrecion del admin (puede haber otras cajas conectadas).
exit 0
