# Restablece Grunflex POS a estado de "instalacion limpia".
# Borra base de datos, usuarios, ventas, logs, backups, licencia y config local.
# La proxima vez que abras el POS te pedira crear el primer usuario administrador.
#
# Uso (ejecutar como administrador):
#   powershell -ExecutionPolicy Bypass -File restablecer-grunflexpos.ps1
#   powershell -ExecutionPolicy Bypass -File restablecer-grunflexpos.ps1 -Silent
#
# El parametro -Silent omite la confirmacion (util para automatizar tests).

[CmdletBinding()]
param(
    [switch]$Silent
)

$ErrorActionPreference = 'Continue'

function Write-Step($msg) {
    Write-Host "==> $msg" -ForegroundColor Cyan
}

function Stop-PosProcesses {
    Write-Step 'Cerrando procesos POS/API activos...'
    Get-Process -Name 'GrunflexPOS2', 'GrunflexPOS.API' -ErrorAction SilentlyContinue |
        ForEach-Object {
            try { $_.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 200; if (!$_.HasExited) { $_.Kill() } } catch { }
        }
    # Tambien paramos el servicio Windows si existe (caja principal).
    $svc = Get-Service -Name 'GrunflexPOS.API' -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        try { Stop-Service -Name 'GrunflexPOS.API' -Force -ErrorAction Stop; Write-Host '   Servicio API detenido.' -ForegroundColor DarkGray } catch { Write-Host "   No se pudo detener el servicio API: $($_.Exception.Message)" -ForegroundColor Yellow }
    }
}

function Remove-FolderQuiet($path) {
    if (Test-Path -LiteralPath $path) {
        try {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
            Write-Host "   Eliminado: $path" -ForegroundColor DarkGray
        } catch {
            Write-Host "   No se pudo eliminar $path : $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}

function Restart-PosApiService {
    $svc = Get-Service -Name 'GrunflexPOS.API' -ErrorAction SilentlyContinue
    if ($svc) {
        try { Start-Service -Name 'GrunflexPOS.API' -ErrorAction Stop; Write-Host '   Servicio API reiniciado.' -ForegroundColor Green } catch { Write-Host "   No se pudo reiniciar el servicio API: $($_.Exception.Message)" -ForegroundColor Yellow }
    }
}

# --- Confirmacion explicita ------------------------------------------------
if (-not $Silent) {
    Write-Host ''
    Write-Host '  RESET DE FABRICA - Grunflex POS' -ForegroundColor Yellow
    Write-Host '  ===============================' -ForegroundColor Yellow
    Write-Host '  Esta operacion BORRA todos los datos de esta PC:'
    Write-Host '    - Base de datos (productos, ventas, usuarios, cajas)'
    Write-Host '    - Configuracion local (ProgramData + LocalAppData)'
    Write-Host '    - Logs, backups, cola offline'
    Write-Host '    - Licencia activada localmente'
    Write-Host ''
    Write-Host '  NO afecta a otras PCs en la red. Solo a esta.'
    Write-Host ''
    $resp = Read-Host 'Escribi BORRAR (en mayusculas) para continuar'
    if ($resp -ne 'BORRAR') {
        Write-Host 'Cancelado.' -ForegroundColor Yellow
        exit 1
    }
}

Stop-PosProcesses

Write-Step 'Eliminando datos en %ProgramData%\GrunflexPOS...'
$pdBase = Join-Path $env:ProgramData 'GrunflexPOS'
Remove-FolderQuiet (Join-Path $pdBase 'data')
Remove-FolderQuiet (Join-Path $pdBase 'logs')
Remove-FolderQuiet (Join-Path $pdBase 'backups')
Remove-FolderQuiet (Join-Path $pdBase 'config')
Remove-FolderQuiet (Join-Path $pdBase 'queue')
Remove-FolderQuiet (Join-Path $pdBase 'license')
# Tambien limpiamos archivos sueltos en la raiz si los hubiera (api.secrets.json, etc.)
Get-ChildItem -Path $pdBase -File -ErrorAction SilentlyContinue |
    ForEach-Object { try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop; Write-Host "   Eliminado: $($_.FullName)" -ForegroundColor DarkGray } catch { } }

Write-Step 'Eliminando datos en %LocalAppData%\GrunflexPOS...'
$ladBase = Join-Path $env:LOCALAPPDATA 'GrunflexPOS'
Remove-FolderQuiet $ladBase

# Tambien borramos appsettings.local.json sueltos que pudieron quedar en %APPDATA% por
# instalaciones legacy (1.0.x).
$appdataLegacy = Join-Path $env:APPDATA 'GrunflexPOS'
if (Test-Path -LiteralPath $appdataLegacy) {
    Remove-FolderQuiet $appdataLegacy
}

Restart-PosApiService

Write-Host ''
Write-Host 'Listo. La proxima vez que abras Grunflex POS te pedira crear el primer' -ForegroundColor Green
Write-Host 'usuario administrador (en el servidor) o configurar la conexion multicaja' -ForegroundColor Green
Write-Host '(en una caja adicional).' -ForegroundColor Green
Write-Host ''
