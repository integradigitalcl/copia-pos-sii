<#
.SYNOPSIS
  Diagnostica y repara la configuración de una caja adicional (multicaja) de Grunflex POS.

.DESCRIPTION
  Casos que cubre:
  - El POS lee dos archivos de configuración (%ProgramData% y %LocalAppData%) y prioriza
    el primero. Si fueron sincronizados a mano o el instalador escribió en uno solo, los
    valores efectivos no son los esperados. Este script los compara y los deja idénticos
    con la cadena de conexión UNC y la URL de API correctas.
  - "Caja no encontrada": elimina el CajaId huérfano de ambos archivos para forzar
    auto-registro en el próximo arranque.
  - "Credenciales incorrectas": consulta la BD central por la red y muestra qué usuarios
    existen realmente. Si el usuario "cajero" no aparece, hay que crearlo en el servidor.
  - "Database is locked": detecta procesos GrunflexPOS2.exe huérfanos y los cierra para
    liberar handles SQLite remanentes en la sesión SMB.
  - Credenciales SMB: re-cachea las credenciales del usuario grunflexshare en cmdkey si
    el acceso a la BD por UNC pide login interactivo.

.PARAMETER ServerIp
  IP del servidor de multicaja (opcional). Si no se pasa, se autodetecta del UNC actual.

.PARAMETER ShareUser
  Usuario local del servidor que tiene permiso al recurso compartido. Default: grunflexshare.

.PARAMETER SharePassword
  Contraseña del ShareUser (la que se asignó al crear el usuario dedicado en el servidor).
  Default: GrunflexLan2025SMB.

.NOTES
  Ejecutar como Administrador en la CAJA ADICIONAL.
  Requiere que el POS haya sido instalado al menos una vez (para usar sus assemblies SQLite).
#>
[CmdletBinding()]
param(
    [string]$ServerIp = "",
    [string]$ShareUser = "grunflexshare",
    [string]$SharePassword = "GrunflexLan2025SMB"
)

$ErrorActionPreference = "Continue"
$ProgressPreference    = "SilentlyContinue"

# Invocar net.exe/cmdkey.exe con argumentos separados: la contraseña por defecto contiene
# '#' y en PowerShell una linea `net use ... pass#2025` trunca en '#' (comentario) → error 86.
$script:NetExe     = Join-Path $env:SystemRoot 'System32\net.exe'
$script:CmdkeyExe  = Join-Path $env:SystemRoot 'System32\cmdkey.exe'

function Write-Section($titulo) {
    Write-Host ""
    Write-Host ("=" * 72) -ForegroundColor Cyan
    Write-Host (" $titulo") -ForegroundColor Cyan
    Write-Host ("=" * 72) -ForegroundColor Cyan
}

function Write-Ok($msg)    { Write-Host "  [ OK ] $msg" -ForegroundColor Green }
function Write-Warn2($msg) { Write-Host "  [WARN] $msg" -ForegroundColor Yellow }
function Write-Err2($msg)  { Write-Host "  [FAIL] $msg" -ForegroundColor Red }
function Write-Info($msg)  { Write-Host "  [info] $msg" -ForegroundColor Gray }

# ---------------------------------------------------------------------------
# 1. Localizar archivos de configuración
# ---------------------------------------------------------------------------
Write-Section "1. Archivos de configuración"

$machinePath = Join-Path $env:ProgramData     "GrunflexPOS\config\appsettings.local.json"
$userPath    = Join-Path $env:LOCALAPPDATA    "GrunflexPOS\config\appsettings.local.json"

$hasMachine = Test-Path $machinePath
$hasUser    = Test-Path $userPath

Write-Info "ProgramData : $machinePath  ->  $(if ($hasMachine) {'EXISTE'} else {'ausente'})"
Write-Info "LocalAppData: $userPath  ->  $(if ($hasUser) {'EXISTE'} else {'ausente'})"

if (-not $hasMachine -and -not $hasUser) {
    Write-Err2 "No se encontró ninguna configuración del POS. Reinstalá el POS antes de continuar."
    exit 2
}

function Read-Json($path) {
    if (-not (Test-Path $path)) { return $null }
    try {
        $raw = Get-Content -Path $path -Raw -Encoding UTF8
        return ($raw | ConvertFrom-Json)
    } catch {
        Write-Warn2 "No se pudo parsear $path : $($_.Exception.Message)"
        return $null
    }
}

$cfgMachine = Read-Json $machinePath
$cfgUser    = Read-Json $userPath

# ---------------------------------------------------------------------------
# 2. Decidir cuál config es la "buena" (UNC al servidor)
# ---------------------------------------------------------------------------
Write-Section "2. Resolución de configuración efectiva"

function Get-Cs($cfg)     { if ($null -ne $cfg) { return $cfg.ConnectionStrings.Default } else { return "" } }
function Get-Api($cfg)    { if ($null -ne $cfg) { return $cfg.Api.BaseUrl } else { return "" } }
function Get-Pago($cfg)   { if ($null -ne $cfg) { return $cfg.Api.PagoBaseUrl } else { return "" } }
function Get-CajaId($cfg) { if ($null -ne $cfg) { return $cfg.CajaId } else { return "" } }

$csMachine = Get-Cs $cfgMachine
$csUser    = Get-Cs $cfgUser

Write-Info "ProgramData.ConnectionString = $csMachine"
Write-Info "LocalAppData.ConnectionString = $csUser"

function Is-Unc($cs) { return $cs -match '\\\\[^\\]+\\GrunflexPOS' }

# Preferencia: la cadena UNC válida. Si ambas son UNC, gana la machine. Si ninguna es UNC, el POS no es multicaja.
$cfgBuena = $null
if (Is-Unc $csMachine) { $cfgBuena = $cfgMachine }
elseif (Is-Unc $csUser) { $cfgBuena = $cfgUser }

if ($null -eq $cfgBuena) {
    Write-Warn2 "Ninguna configuración apunta a un UNC \\\\servidor\\GrunflexPOS. Este equipo no parece estar configurado como caja adicional."
    Write-Warn2 "Si querés conectarlo como caja adicional, ejecutá primero el flujo 'Conectar más cajas' en la caja principal."
    exit 1
}

# Sanear segmento \data\ heredado.
$cs = $cfgBuena.ConnectionStrings.Default
$csSan = $cs -replace '\\GrunflexPOS\\data\\', '\GrunflexPOS\'
if ($csSan -ne $cs) {
    Write-Warn2 "Cadena con \data\ obsoleto detectada. Saneando: $csSan"
    $cfgBuena.ConnectionStrings.Default = $csSan
}

# Extraer host del UNC.
$uncHost = $null
if ($cfgBuena.ConnectionStrings.Default -match '\\\\([^\\]+)\\GrunflexPOS') {
    $uncHost = $matches[1]
}

if ([string]::IsNullOrWhiteSpace($ServerIp)) { $ServerIp = $uncHost }
Write-Info "Servidor detectado: $uncHost"
if ($ServerIp -ne $uncHost) {
    Write-Warn2 "El parámetro -ServerIp ($ServerIp) no coincide con el UNC ($uncHost). Uso $ServerIp por la URL de API."
}

# Sanear URL de API: si es localhost, usar la del servidor.
function Is-LocalUrl($u) { return ($u -match '//(localhost|127\.0\.0\.1|\[::1\]|::1)') }
if (Is-LocalUrl $cfgBuena.Api.BaseUrl) {
    $nuevoBase = "http://${ServerIp}:7279/"
    Write-Warn2 "Api.BaseUrl estaba en localhost. Reemplazando por $nuevoBase"
    $cfgBuena.Api.BaseUrl = $nuevoBase
}
if (Is-LocalUrl $cfgBuena.Api.PagoBaseUrl) {
    $nuevoPago = "http://${ServerIp}:7279/api/pago"
    Write-Warn2 "Api.PagoBaseUrl estaba en localhost. Reemplazando por $nuevoPago"
    $cfgBuena.Api.PagoBaseUrl = $nuevoPago
}

Write-Ok "Configuración a aplicar:"
Write-Info "  ConnectionString: $($cfgBuena.ConnectionStrings.Default)"
Write-Info "  Api.BaseUrl     : $($cfgBuena.Api.BaseUrl)"
Write-Info "  Api.PagoBaseUrl : $($cfgBuena.Api.PagoBaseUrl)"
Write-Info "  CajaId actual   : $(Get-CajaId $cfgBuena)"

# ---------------------------------------------------------------------------
# 3. Matar procesos huérfanos
# ---------------------------------------------------------------------------
Write-Section "3. Procesos GrunflexPOS2 huérfanos"
$procs = Get-Process -Name "GrunflexPOS2" -ErrorAction SilentlyContinue
if ($procs) {
    Write-Warn2 "Procesos POS activos encontrados (PIDs: $($procs.Id -join ', ')). Cerrando para liberar la BD..."
    foreach ($p in $procs) {
        try { $p.Kill(); $p.WaitForExit(5000); Write-Ok "PID $($p.Id) cerrado." }
        catch { Write-Err2 "No se pudo cerrar PID $($p.Id): $($_.Exception.Message)" }
    }
    Start-Sleep -Seconds 2
} else {
    Write-Ok "No hay procesos POS activos."
}

# ---------------------------------------------------------------------------
# 4. Cachear credenciales SMB al servidor
# ---------------------------------------------------------------------------
Write-Section "4. Credenciales SMB (cmdkey)"
$target = "\\$ServerIp"
$shareRoot = "\\$ServerIp\GrunflexPOS"
$cmdkeyUser = "$ServerIp\$ShareUser"
try {
    & $script:CmdkeyExe @("/delete:$target") 2>$null | Out-Null
    & $script:CmdkeyExe @("/add:$target", "/user:$cmdkeyUser", "/pass:$SharePassword") | Out-Null
    Write-Ok "Credenciales cacheadas para $target con usuario $cmdkeyUser."
} catch {
    Write-Warn2 "No se pudo manipular cmdkey: $($_.Exception.Message)"
}

# Soltar cualquier mapeo previo y volver a conectar (argumentos separados → '#' seguro).
try { & $script:NetExe @('use', $target, '/delete', '/y') 2>$null | Out-Null } catch { }
try { & $script:NetExe @('use', $shareRoot, '/delete', '/y') 2>$null | Out-Null } catch { }

$ne = & $script:NetExe @('use', $shareRoot, "/user:$cmdkeyUser", $SharePassword, '/persistent:yes') 2>&1
if ($LASTEXITCODE -eq 0) { Write-Ok "Sesión SMB establecida a $shareRoot." }
else { Write-Warn2 "net use devolvió (exit=$LASTEXITCODE): $ne — Si pegaste la clave a mano, el # debe ir entre comillas." }

# ---------------------------------------------------------------------------
# 5. Acceso real al recurso y verificación de la BD
# ---------------------------------------------------------------------------
Write-Section "5. Acceso al recurso compartido"
$dbPath    = "\\$ServerIp\GrunflexPOS\grunflex.db"

if (Test-Path $shareRoot) { Write-Ok "Recurso accesible: $shareRoot" }
else { Write-Err2 "No se pudo listar $shareRoot. Revisá que el servidor tenga compartida la carpeta y que estés en la misma red." }

if (Test-Path $dbPath) {
    $size = (Get-Item $dbPath).Length
    Write-Ok "BD encontrada: $dbPath ($([math]::Round($size/1KB,1)) KB)"
} else {
    Write-Err2 "BD no encontrada en $dbPath. Sin esto no hay multicaja posible. Detenido."
    exit 3
}

# ---------------------------------------------------------------------------
# 6. Inspección de la BD: Usuarios y Cajas
# ---------------------------------------------------------------------------
Write-Section "6. Inspección de la base central"

function Find-SqliteDll {
    $candidatos = @(
        Join-Path ${env:ProgramFiles}     "GrunflexPOS\Microsoft.Data.Sqlite.dll",
        Join-Path ${env:ProgramFiles(x86)} "GrunflexPOS\Microsoft.Data.Sqlite.dll"
    )
    foreach ($c in $candidatos) { if ($c -and (Test-Path $c)) { return $c } }
    return $null
}

$sqliteDll = Find-SqliteDll
if (-not $sqliteDll) {
    Write-Warn2 "No se encontró Microsoft.Data.Sqlite.dll. Se omite la consulta directa a la BD."
} else {
    try {
        $posDir = Split-Path $sqliteDll
        # Cargar dependencias previas (mismo orden que el POS).
        $deps = @(
            "SQLitePCLRaw.core.dll",
            "SQLitePCLRaw.batteries_v2.dll",
            "SQLitePCLRaw.provider.e_sqlite3.dll",
            "Microsoft.Data.Sqlite.dll"
        )
        foreach ($d in $deps) {
            $p = Join-Path $posDir $d
            if (Test-Path $p) { [void][System.Reflection.Assembly]::LoadFrom($p) }
        }

        $conn = New-Object Microsoft.Data.Sqlite.SqliteConnection
        $conn.ConnectionString = "Data Source=$dbPath;Mode=ReadOnly;Cache=Shared;Default Timeout=10"
        $conn.Open()
        Write-Ok "Conexión a la BD central abierta en modo lectura."

        # Listar usuarios
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = "SELECT Username, Rol FROM Usuarios ORDER BY Username COLLATE NOCASE;"
        $reader = $cmd.ExecuteReader()
        $users = New-Object System.Collections.Generic.List[string]
        while ($reader.Read()) {
            $users.Add("$($reader['Username']) ($($reader['Rol']))")
        }
        $reader.Close()

        if ($users.Count -eq 0) {
            Write-Err2 "La tabla Usuarios está VACÍA en la BD central. Andá a la caja principal (servidor) y creá un usuario admin antes de loguearte desde la caja adicional."
        } else {
            Write-Ok "Usuarios en la BD central:"
            foreach ($u in $users) { Write-Info "    - $u" }
            if (-not ($users -match '(?i)^cajero ')) {
                Write-Warn2 "El usuario 'cajero' NO existe en la BD central. Probá con uno de los listados o creálo en el servidor."
            } else {
                Write-Ok "Usuario 'cajero' presente en la BD central."
            }
        }

        # Listar cajas
        $cmd2 = $conn.CreateCommand()
        $cmd2.CommandText = "SELECT Id, Nombre, Activa FROM Cajas ORDER BY Nombre COLLATE NOCASE;"
        $reader2 = $cmd2.ExecuteReader()
        $cajas = New-Object System.Collections.Generic.List[psobject]
        while ($reader2.Read()) {
            $cajas.Add([pscustomobject]@{
                Id     = "$($reader2['Id'])"
                Nombre = "$($reader2['Nombre'])"
                Activa = "$($reader2['Activa'])"
            })
        }
        $reader2.Close()

        Write-Ok "Cajas en la BD central:"
        foreach ($c in $cajas) { Write-Info "    - $($c.Id)  $($c.Nombre)  activa=$($c.Activa)" }

        $cajaIdLocal = (Get-CajaId $cfgBuena)
        if (-not [string]::IsNullOrWhiteSpace($cajaIdLocal)) {
            $coincide = $cajas | Where-Object { $_.Id -eq $cajaIdLocal }
            if (-not $coincide) {
                Write-Warn2 "El CajaId local ($cajaIdLocal) NO existe en la BD central. Lo borro para forzar auto-registro."
                $cfgBuena.CajaId = ""
            } else {
                Write-Ok "El CajaId local existe en la BD central."
            }
        } else {
            Write-Info "No hay CajaId local: el POS auto-registrará una caja con el nombre del equipo en el próximo arranque."
        }

        $conn.Close()
    } catch {
        Write-Err2 "Falló la inspección de la BD: $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------------------
# 7. Persistir configuración saneada en AMBAS ubicaciones
# ---------------------------------------------------------------------------
Write-Section "7. Persistencia de configuración"

$jsonFinal = $cfgBuena | ConvertTo-Json -Depth 5

foreach ($p in @($machinePath, $userPath)) {
    try {
        $dir = Split-Path $p -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Set-Content -Path $p -Value $jsonFinal -Encoding UTF8 -Force
        Write-Ok "Escrito: $p"
    } catch {
        Write-Err2 "No se pudo escribir $p : $($_.Exception.Message) (¿necesitás permisos de admin?)"
    }
}

# También limpiar el marker de plantilla aplicada por si quedó stale.
$tplMarker = Join-Path $env:ProgramData "GrunflexPOS\config\grunflex-terminal.json.aplicado"
if (Test-Path $tplMarker) {
    try { Remove-Item $tplMarker -Force; Write-Ok "Marker de plantilla eliminado: $tplMarker" }
    catch { Write-Warn2 "No se pudo eliminar $tplMarker : $($_.Exception.Message)" }
}

Write-Section "Resumen"
Write-Ok "Configuración sincronizada en ProgramData y LocalAppData."
Write-Ok "Procesos POS huérfanos cerrados (si los había)."
Write-Ok "Credenciales SMB cacheadas para $target con usuario $cmdkeyUser."
Write-Host ""
Write-Host "Próximos pasos:" -ForegroundColor Cyan
Write-Host "  1. Abrir GrunflexPOS2 desde el menú inicio."
Write-Host "  2. En la pantalla de login usar exactamente un Username de la lista de arriba (case-sensitive)."
Write-Host "  3. Si el POS pide volver a registrarse, dejá que termine — quedará una Caja nueva con el nombre de este equipo."
Write-Host ""
