# =====================================================================
# REPARAR-CONEXION-MULTICAJA.ps1
# Corrige el archivo appsettings.local.json de una caja adicional cuando
# tiene problemas heredados de instaladores antiguos:
#
#   1) Cadena de conexion con "\\IP\GrunflexPOS\data\grunflex.db"
#      (sobra \data\: el share YA apunta a la carpeta data del servidor)
#
#   2) "Api":"BaseUrl" / "Api":"PagoBaseUrl" apuntando a localhost o
#      127.0.0.1 cuando la BD es UNC. La API real corre en el servidor,
#      NO en la caja adicional, asi que esa URL debe apuntar al mismo
#      host del UNC.
#
# USO MANUAL:
#   Click derecho > "Ejecutar con PowerShell" (no requiere admin)
#
# USO SILENCIOSO (lo invoca el instalador al final del setup):
#   powershell -NoProfile -File reparar-conexion-multicaja.ps1 -Silent
# =====================================================================

param(
    [switch] $Silent
)

$ErrorActionPreference = 'Continue'

function Write-Hdr($t) {
    Write-Host ""
    Write-Host ("=" * 70) -ForegroundColor Cyan
    Write-Host (" {0}" -f $t) -ForegroundColor Cyan
    Write-Host ("=" * 70) -ForegroundColor Cyan
}

Write-Hdr "Reparador de conexion multicaja - Grunflex POS"

# Localizar candidatos: %LocalAppData% del usuario actual + Program Files (legacy).
$rutas = @(
    (Join-Path $env:ProgramData 'GrunflexPOS\config\appsettings.local.json'),
    (Join-Path $env:LOCALAPPDATA 'GrunflexPOS\config\appsettings.local.json'),
    'C:\Program Files\GrunflexPOS\appsettings.local.json',
    'C:\Program Files (x86)\GrunflexPOS\appsettings.local.json'
)

# Tambien escanear todos los perfiles de usuario por si el instalador escribio en otro.
try {
    $users = Get-ChildItem 'C:\Users' -Directory -ErrorAction SilentlyContinue |
             Where-Object { $_.Name -notin @('Default','Public','All Users','Default User') }
    foreach ($u in $users) {
        $p = Join-Path $u.FullName 'AppData\Local\GrunflexPOS\config\appsettings.local.json'
        if ($rutas -notcontains $p) { $rutas += $p }
    }
} catch {}

Write-Host ""
Write-Host "Buscando archivos appsettings.local.json en:" -ForegroundColor Yellow
$rutas | ForEach-Object { Write-Host "  - $_" }

$existentes = $rutas | Where-Object { Test-Path $_ }
if (-not $existentes) {
    Write-Host ""
    Write-Host "[INFO] No se encontro ningun appsettings.local.json." -ForegroundColor Yellow
    Write-Host "       Si esta es una caja adicional, abre el POS y usa:" -ForegroundColor Yellow
    Write-Host "       Configuracion -> Administrar caja -> Conectar a caja principal" -ForegroundColor Yellow
    Write-Host ""
    if (-not $Silent) { Read-Host "Presiona Enter para cerrar" }
    exit 0
}

# Devuelve el host del primer UNC encontrado en una cadena ("\\HOST\share\..." -> "HOST"). Vacio si no hay.
function Get-UncHost([string]$texto) {
    if ([string]::IsNullOrWhiteSpace($texto)) { return '' }
    $m = [regex]::Match($texto, '\\{2,4}([A-Za-z0-9_.\-]+)\\{1,2}GrunflexPOS', 'IgnoreCase')
    if ($m.Success) { return $m.Groups[1].Value }
    return ''
}

$reparados = 0
$yaCorrectos = 0
$conRespaldo = @()

foreach ($ruta in $existentes) {
    Write-Hdr "Archivo: $ruta"
    $contenido = Get-Content -Raw -Path $ruta -ErrorAction Stop
    Write-Host "Contenido actual:"
    Write-Host $contenido -ForegroundColor Gray

    $nuevo = $contenido
    $cambios = @()

    # ---- 1) Quitar el \data\ del UNC en ConnectionStrings ----
    $patBackslashEsc = '\\\\GrunflexPOS\\\\data\\\\'        # JSON: \\GrunflexPOS\\data\\
    $repBackslashEsc = '\\\\GrunflexPOS\\\\'
    $patBackslashRaw = '\\GrunflexPOS\\data\\'              # ruta cruda: \GrunflexPOS\data\
    $repBackslashRaw = '\\GrunflexPOS\\'

    if ($nuevo -match $patBackslashEsc) {
        $nuevo = [regex]::Replace($nuevo, $patBackslashEsc, $repBackslashEsc, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        $cambios += 'ConnectionString: removido segmento \\data\\ (escapado)'
    }
    if ($nuevo -match $patBackslashRaw) {
        $nuevo = [regex]::Replace($nuevo, $patBackslashRaw, $repBackslashRaw, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        $cambios += 'ConnectionString: removido segmento \data\ (sin escapar)'
    }

    # ---- 2) Si la BD es UNC pero Api:BaseUrl apunta a localhost, arreglar ----
    $unc = Get-UncHost $nuevo
    if ($unc -ne '') {
        # BaseUrl y PagoBaseUrl pueden quedar como //localhost o //127.0.0.1
        $reLoc = '(https?:)\/\/(localhost|127\.0\.0\.1|\[::1\]|::1)'
        if ($nuevo -match $reLoc) {
            $nuevo = [regex]::Replace($nuevo, $reLoc, ('$1//' + $unc), [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            $cambios += "Api:BaseUrl/PagoBaseUrl: localhost -> $unc"
        }
    }

    if ($cambios.Count -eq 0) {
        Write-Host ""
        Write-Host "[OK] No requiere ningun cambio." -ForegroundColor Green
        $yaCorrectos++
        continue
    }

    # Respaldo + escritura
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $respaldo = "$ruta.bak.$stamp"
    Copy-Item -Path $ruta -Destination $respaldo -Force
    $conRespaldo += $respaldo

    Set-Content -Path $ruta -Value $nuevo -Encoding UTF8 -NoNewline

    Write-Host ""
    Write-Host "[REPARADO] Cambios aplicados:" -ForegroundColor Green
    $cambios | ForEach-Object { Write-Host "   - $_" -ForegroundColor Green }
    Write-Host ""
    Write-Host "Respaldo: $respaldo" -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "Nuevo contenido:" -ForegroundColor Yellow
    Write-Host $nuevo -ForegroundColor Gray
    $reparados++
}

Write-Hdr "Resumen"
Write-Host ("Archivos reparados      : {0}" -f $reparados)
Write-Host ("Archivos ya correctos   : {0}" -f $yaCorrectos)
if ($conRespaldo.Count -gt 0) {
    Write-Host ""
    Write-Host "Respaldos creados (si algo falla, restaure desde estos):" -ForegroundColor DarkGray
    $conRespaldo | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
}

Write-Host ""
if ($reparados -gt 0) {
    Write-Host "Listo. Cierra el POS si esta abierto y vuelve a abrirlo." -ForegroundColor Green
} else {
    Write-Host "No habia nada que reparar." -ForegroundColor Green
}
Write-Host ""
if (-not $Silent) { Read-Host "Presiona Enter para cerrar" }
