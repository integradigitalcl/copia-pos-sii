# =====================================================================
# HABILITAR-ACCESO-MULTICAJA-CLIENTE.ps1
# Prepara esta PC (caja adicional) para conectarse al recurso compartido
# de la caja principal. Hace tres cosas:
#
#   1) AllowInsecureGuestAuth=1 en LanmanWorkstation
#      (compatibilidad por si el servidor expone el share como Everyone).
#
#   2) Cachea las credenciales del usuario local "grunflexshare" en
#      cmdkey para que Windows pueda abrir \\IP\GrunflexPOS sin pedir
#      login interactivo. Este es el modelo definitivo desde 1.3.7+:
#      el servidor crea el usuario dedicado, el cliente lo cachea.
#
#   3) Verifica que la sesion SMB queda viva con net use.
#
# Requiere ejecucion como administrador.
# =====================================================================

param(
    [switch] $Silent,
    [string] $ServerIp = "",
    [string] $ShareUser = "grunflexshare",
    [string] $SharePassword = "GrunflexLan2025SMB"
)

$ErrorActionPreference = 'Continue'

# net.exe / cmdkey.exe deben invocarse con argumentos separados (& net.exe @(...)).
# Si la contraseña contiene '#', una linea tipo: net use ... GrunflexShare#2025
# en PowerShell interpreta '#' como inicio de comentario y TRUNCA la contraseña → error 86.
$script:NetExe   = Join-Path $env:SystemRoot 'System32\net.exe'
$script:CmdkeyExe = Join-Path $env:SystemRoot 'System32\cmdkey.exe'

function Say($t, [ConsoleColor]$c = [ConsoleColor]::Gray) {
    if (-not $Silent) { Write-Host $t -ForegroundColor $c }
}

function Hdr($t) {
    if ($Silent) { return }
    Write-Host ""
    Write-Host ("=" * 70) -ForegroundColor Cyan
    Write-Host " $t" -ForegroundColor Cyan
    Write-Host ("=" * 70) -ForegroundColor Cyan
}

Hdr "Habilitar acceso a multicaja (cliente SMB)"

# Verificar permisos elevados
$elev = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $elev) {
    Say "ERROR: ejecute como administrador (clic derecho > Ejecutar como administrador)." Red
    if (-not $Silent) { Read-Host "Presione Enter para cerrar" }
    exit 1
}

# ---------------------------------------------------------------------
# 1) AllowInsecureGuestAuth
# ---------------------------------------------------------------------
$key = "HKLM:\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters"
try {
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    New-ItemProperty -Path $key -Name "AllowInsecureGuestAuth" -Value 1 -PropertyType DWord -Force | Out-Null
    Say "  - AllowInsecureGuestAuth = 1 aplicado en LanmanWorkstation" Green
} catch {
    Say "ERROR aplicando registry: $_" Red
    exit 2
}

# ---------------------------------------------------------------------
# 2) Cachear credenciales del usuario dedicado del servidor
# ---------------------------------------------------------------------
# Si no nos pasaron IP, intentamos leerla del config para no quedar mancos.
if ([string]::IsNullOrWhiteSpace($ServerIp)) {
    foreach ($cfgPath in @(
        (Join-Path $env:ProgramData 'GrunflexPOS\config\appsettings.local.json'),
        (Join-Path $env:LOCALAPPDATA 'GrunflexPOS\config\appsettings.local.json')
    )) {
        if (Test-Path -LiteralPath $cfgPath) {
            try {
                $cfg = Get-Content -LiteralPath $cfgPath -Raw | ConvertFrom-Json
                $cs = $cfg.ConnectionStrings.Default
                if ($cs -match '\\\\([^\\]+)\\') {
                    $ServerIp = $Matches[1]
                    Say "  - IP del servidor detectada desde config: $ServerIp" DarkGray
                    break
                }
            } catch { }
        }
    }
}

if ([string]::IsNullOrWhiteSpace($ServerIp)) {
    Say "  ! No se especifico ServerIp y no se pudo deducir desde la config." Yellow
    Say "    Las credenciales NO quedaron cacheadas. Volve a correr este script con:" Yellow
    Say "      .\habilitar-acceso-multicaja-cliente.ps1 -ServerIp 192.168.1.10" Yellow
} else {
    $target = "\\$ServerIp"
    $shareTarget = "\\$ServerIp\GrunflexPOS"
    $cmdkeyUser = "$ServerIp\$ShareUser"

    # Limpiar credenciales viejas; arrancamos siempre desde cero.
    try { & $script:CmdkeyExe @("/delete:$target") 2>$null | Out-Null } catch {}
    try { & $script:NetExe @('use', $target, '/delete', '/y') 2>$null | Out-Null } catch {}
    try { & $script:NetExe @('use', $shareTarget, '/delete', '/y') 2>$null | Out-Null } catch {}

    # Estrategia principal: usuario dedicado en el servidor (grunflexshare).
    try {
        & $script:CmdkeyExe @("/add:$target", "/user:$cmdkeyUser", "/pass:$SharePassword") | Out-Null
        Say "  - Credenciales SMB cacheadas para $target ($cmdkeyUser)." Green
    } catch {
        Say "  ! No se pudo manipular cmdkey: $($_.Exception.Message)" Yellow
    }

    # Mapear directamente al recurso UNC del share (no solo \\IP).
    $netOut = & $script:NetExe @('use', $shareTarget, "/user:$cmdkeyUser", $SharePassword, '/persistent:yes') 2>&1
    if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $shareTarget)) {
        Say "  - Sesion SMB OK: $shareTarget" Green
    } else {
        Say "  ! Fallo autenticacion con $ShareUser (exit=$LASTEXITCODE)." Yellow
        Say "    Salida: $netOut" Yellow
        Say "    Tip: si pegaste la contraseña a mano en PowerShell, el caracter # debe ir ENTRE COMILLAS." Yellow

        # Estrategia secundaria: acceso anonimo (Guest) — puede tardar; timeout corto.
        if (-not $Silent) {
            Say "  - Probando acceso anonimo (Guest), max 12s..." DarkGray
            try {
                $p = Start-Process -FilePath $script:NetExe `
                    -ArgumentList @('use', $shareTarget, '/user:', '') `
                    -WindowStyle Hidden -PassThru -ErrorAction Stop
                if (-not $p.WaitForExit(12000)) {
                    try { $p.Kill() } catch {}
                    Say "  ! Acceso anonimo: timeout." Yellow
                } elseif ($p.ExitCode -eq 0 -and (Test-Path -LiteralPath $shareTarget)) {
                    Say "  - Acceso anonimo OK (Guest). No se usan credenciales dedicadas." Green
                    $netOut = @()
                }
            } catch {
                Say "  ! Acceso anonimo: $($_.Exception.Message)" Yellow
            }
        }

        if (-not (Test-Path -LiteralPath $shareTarget)) {
            Say "    Posibles causas:" Yellow
            Say "      - En el SERVIDOR: usuario '$ShareUser' inexistente o clave distinta." Yellow
            Say "        Ejecuta en el servidor (PowerShell admin) el bloque que crea ${ShareUser}." Yellow
            Say "      - IP equivocada / firewall TCP 445 / servidor apagado." Yellow
            Say "      - Menu Inicio -> Grunflex POS -> 'Habilitar recurso GrunflexPOS' (admin) en el servidor." Yellow
        }
    }
}

# ---------------------------------------------------------------------
# 4) Reiniciar LanmanWorkstation para que AllowInsecureGuestAuth aplique
# ---------------------------------------------------------------------
try {
    $svc = Get-Service LanmanWorkstation -ErrorAction Stop
    $deps = Get-Service -Name $svc.Name -DependentServices -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Running' }
    foreach ($d in $deps) { try { Stop-Service $d.Name -Force -ErrorAction SilentlyContinue } catch {} }
    try { Stop-Service LanmanWorkstation -Force -ErrorAction Stop } catch {
        Say "  ! No se pudo detener LanmanWorkstation; reinicia Windows para que AllowInsecureGuestAuth aplique." Yellow
        if (-not $Silent) { Read-Host "Presione Enter para cerrar" }
        exit 0
    }
    Start-Service LanmanWorkstation
    foreach ($d in $deps) { try { Start-Service $d.Name -ErrorAction SilentlyContinue } catch {} }
    Say "  - Servicio Workstation reiniciado." Green
} catch {
    Say "  ! No se pudo reiniciar el servicio: $_. Reinicia Windows para que aplique el cambio." Yellow
}

if (-not $Silent) {
    Write-Host ""
    Write-Host "Listo. Abri el POS y deberia conectar al servidor." -ForegroundColor Cyan
    Write-Host "Si sigue fallando, ejecuta el atajo 'Diagnosticar multicaja' del menu inicio." -ForegroundColor Cyan
    Write-Host ""
    Read-Host "Presione Enter para cerrar"
}
