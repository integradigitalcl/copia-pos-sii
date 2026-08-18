$ErrorActionPreference = "Continue"
$ProgressPreference    = "SilentlyContinue"
$WarningPreference     = "SilentlyContinue"
$ConfirmPreference     = "None"

# ==============================================================================
# Grunflex POS - Caja principal:
#   - Crea carpeta de datos machine-wide en %ProgramData%\GrunflexPOS\data
#   - Migra base SQLite existente desde %LocalAppData% (compat legacy)
#   - Comparte la carpeta via SMB (\\servidor\GrunflexPOS)
#   - Permisos NTFS independientes del idioma
#   - Abre firewall TCP 7279 (API)
#   - Instala/actualiza el servicio Windows "GrunflexPOSAPI" con recovery automatico
# ==============================================================================

# Logs siempre van a la carpeta del usuario que ejecuta el instalador
$logPath = Join-Path $env:LOCALAPPDATA "GrunflexPOS\logs\setup-multicaja.log"
$logDir  = Split-Path $logPath -Parent
if (-not (Test-Path $logDir)) {
    try { New-Item -ItemType Directory -Path $logDir -Force | Out-Null } catch {}
}
"--- $(Get-Date -Format s) ---" | Out-File -FilePath $logPath -Append -Encoding utf8

# Rotacion simple: si el log pasa de 5MB, mover a .1 y empezar de nuevo. Borra logs > 30 dias.
try {
    if ((Test-Path $logPath) -and ((Get-Item $logPath).Length -gt 5242880)) {
        $rolled = "$logPath.1"
        if (Test-Path $rolled) { Remove-Item $rolled -Force -ErrorAction SilentlyContinue }
        Move-Item -Path $logPath -Destination $rolled -Force -ErrorAction SilentlyContinue
    }
    Get-ChildItem -Path $logDir -Filter "setup-multicaja*.log*" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-30) } |
        Remove-Item -Force -ErrorAction SilentlyContinue
} catch {}

function Log-Step([string]$msg) {
    Write-Host $msg
    try { "$(Get-Date -Format s) $msg" | Out-File -FilePath $logPath -Append -Encoding utf8 } catch {}
}

function Run-Net {
    param([string] $ArgsLine)
    try {
        $out = cmd /c "netsh $ArgsLine 2>&1"
        $rc  = $LASTEXITCODE
        try { "netsh $ArgsLine -> rc=$rc | $($out -join ' ')" | Out-File -FilePath $logPath -Append -Encoding utf8 } catch {}
        return ($rc -eq 0)
    } catch {
        try { "netsh $ArgsLine EXC: $($_.Exception.Message)" | Out-File -FilePath $logPath -Append -Encoding utf8 } catch {}
        return $false
    }
}

function Run-Sc {
    param([string] $ArgsLine)
    try {
        $out = cmd /c "sc.exe $ArgsLine 2>&1"
        $rc  = $LASTEXITCODE
        try { "sc.exe $ArgsLine -> rc=$rc | $($out -join ' ')" | Out-File -FilePath $logPath -Append -Encoding utf8 } catch {}
        return ($rc -eq 0)
    } catch {
        try { "sc.exe $ArgsLine EXC: $($_.Exception.Message)" | Out-File -FilePath $logPath -Append -Encoding utf8 } catch {}
        return $false
    }
}

Write-Host "==============================================="
Write-Host " Grunflex POS - configurar caja principal"
Write-Host "==============================================="
Write-Host ""

# Rutas canonicas
$shareName    = "GrunflexPOS"
$dataFolder   = Join-Path $env:ProgramData "GrunflexPOS\data"
$logsFolder   = Join-Path $env:ProgramData "GrunflexPOS\logs"
$legacyFolder = Join-Path $env:LOCALAPPDATA "GrunflexPOS\data"
$serviceName  = "GrunflexPOSAPI"

# [1/6] Crear estructura ProgramData
Log-Step "[1/6] Carpeta de datos machine-wide: $dataFolder"
try {
    if (-not (Test-Path $dataFolder)) { New-Item -ItemType Directory -Path $dataFolder -Force | Out-Null }
    if (-not (Test-Path $logsFolder)) { New-Item -ItemType Directory -Path $logsFolder -Force | Out-Null }
} catch {
    Log-Step "  [ERROR] No se pudo crear carpeta de datos: $($_.Exception.Message)"
}

# [2/6] Migracion legacy LocalAppData -> ProgramData (tanto grunflex.db POS como grunflex_api.db).
# Tambien recorre carpetas de otros usuarios en C:\Users\*\AppData\Local\GrunflexPOS\data si la actual no tiene datos.
Log-Step "[2/6] Migrando datos legacy (si existen)..."
try {
    $candidateFolders = @()
    if (Test-Path $legacyFolder) { $candidateFolders += $legacyFolder }

    $usersRoot = Join-Path $env:SystemDrive "\Users"
    if (Test-Path $usersRoot) {
        Get-ChildItem -Path $usersRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $cand = Join-Path $_.FullName "AppData\Local\GrunflexPOS\data"
            if ((Test-Path $cand) -and ($candidateFolders -notcontains $cand)) {
                $candidateFolders += $cand
            }
        }
    }

    foreach ($cand in $candidateFolders) {
        Log-Step "  Revisando $cand"
        Get-ChildItem -Path $cand -File -ErrorAction SilentlyContinue | ForEach-Object {
            $destFile = Join-Path $dataFolder $_.Name
            if (-not (Test-Path $destFile)) {
                try {
                    Copy-Item -Path $_.FullName -Destination $destFile -Force -ErrorAction SilentlyContinue
                    Log-Step "    Migrado: $($_.Name)"
                } catch {
                    Log-Step "    [WARN] No se migro $($_.Name): $($_.Exception.Message)"
                }
            }
        }
    }
    if ($candidateFolders.Count -eq 0) {
        Log-Step "  No hay datos legacy que migrar."
    }
} catch {
    Log-Step "  [WARN] Migracion: $($_.Exception.Message)"
}

# [2.5/6] Usuario local dedicado al share multicaja. Sin este usuario las cajas
# adicionales no pueden autenticar contra el share (el modelo "Guest" en Windows
# 10/11 modernos está muy restringido). El usuario tiene password fijo conocido
# por el cliente porque el modelo Eleventa expone el share solo en LAN cerrada.
$shareUser = "grunflexshare"
$sharePwd  = "GrunflexLan2025SMB"
$machineShareUser = "$env:COMPUTERNAME\$shareUser"
Log-Step "[2.5/6] Usuario local dedicado '$shareUser'..."
try {
    $securePwd = ConvertTo-SecureString $sharePwd -AsPlainText -Force
    $existing = Get-LocalUser -Name $shareUser -ErrorAction SilentlyContinue
    if ($existing) {
        Set-LocalUser -Name $shareUser -Password $securePwd
        if (-not $existing.Enabled) { Enable-LocalUser -Name $shareUser }
        Log-Step "  Usuario $shareUser ya existía: password sincronizado."
    } else {
        New-LocalUser -Name $shareUser -Password $securePwd `
            -FullName "Grunflex Share Account" `
            -Description "Usuario dedicado al share multicaja Grunflex POS" `
            -PasswordNeverExpires -AccountNeverExpires `
            -UserMayNotChangePassword -ErrorAction Stop | Out-Null
        Log-Step "  Usuario $shareUser creado."
    }
} catch {
    Log-Step "  [WARN] No se pudo crear/sincronizar $shareUser : $($_.Exception.Message)"
}

# [3/6] Recurso SMB (sobre ProgramData, no LocalAppData)
# IMPORTANTE: en Windows en español "Everyone" no se resuelve (se llama "Todos"). Usamos el SID
# S-1-1-0 y lo traducimos al nombre localizado para ser language-independent.
Log-Step "[3/6] Configurando recurso SMB '$shareName' sobre $dataFolder..."
try {
    $sidEveryone  = New-Object System.Security.Principal.SecurityIdentifier 'S-1-1-0'
    $nameEveryone = $sidEveryone.Translate([System.Security.Principal.NTAccount]).Value
    Log-Step "  SID S-1-1-0 resuelto a '$nameEveryone'"

    # Borrar share si existe (idempotente, evita conflictos de path)
    try { Remove-SmbShare -Name $shareName -Force -ErrorAction Stop | Out-Null } catch {}

    # Crear share SIN -ChangeAccess (evita resolver Everyone al construir)
    $created = $false
    try {
        New-SmbShare -Name $shareName -Path $dataFolder -Description "Grunflex POS multicaja" -ErrorAction Stop | Out-Null
        $created = $true
        Log-Step "  Share creado."
    } catch {
        Log-Step "  [WARN] New-SmbShare fallo: $($_.Exception.Message). Probando net share..."
        $out = cmd /c "net share $shareName=`"$dataFolder`" /REMARK:`"Grunflex POS multicaja`" 2>&1"
        $rc  = $LASTEXITCODE
        Log-Step "  net share rc=$rc out=$($out -join ' ')"
        $created = ($rc -eq 0)
    }

    # Conceder Change al grupo localizado (Todos / Everyone) - modelo Guest legado
    if ($created) {
        try {
            Grant-SmbShareAccess -Name $shareName -AccountName $nameEveryone -AccessRight Change -Force -ErrorAction Stop | Out-Null
            Log-Step "  Permisos compartidos: $nameEveryone=Change OK"
        } catch {
            Log-Step "  [WARN] Grant-SmbShareAccess fallo: $($_.Exception.Message)"
            cmd /c "net share $shareName /GRANT:`"$nameEveryone`",CHANGE 2>&1" | Out-Null
        }

        # Conceder Change al usuario dedicado (modelo principal usado por el cliente).
        try {
            Grant-SmbShareAccess -Name $shareName -AccountName $machineShareUser -AccessRight Change -Force -ErrorAction Stop | Out-Null
            Log-Step "  Permisos compartidos: $machineShareUser=Change OK"
        } catch {
            Log-Step "  [WARN] Grant-SmbShareAccess para $machineShareUser falló: $($_.Exception.Message)"
            cmd /c "net share $shareName /GRANT:`"$machineShareUser`",CHANGE 2>&1" | Out-Null
        }
    }
} catch {
    Log-Step "  [WARN] SMB: $($_.Exception.Message)"
}

# [3.1/6] SMB servidor en LAN: permitir clientes sin negociación estricta; reiniciar servicio para aplicar share.
Log-Step "[3.1/6] Ajuste SMB servidor (solo LAN) + reinicio LanmanServer..."
try {
    Set-SmbServerConfiguration -EncryptData $false -RejectUnencryptedAccess $false -Force -ErrorAction SilentlyContinue
    Log-Step "  Set-SmbServerConfiguration (cifrado flexible / acceso sin rechazo por cifrado)."
} catch {
    Log-Step "  [WARN] Set-SmbServerConfiguration: $($_.Exception.Message)"
}
try {
    Restart-Service lanmanserver -Force -ErrorAction Stop
    Start-Sleep -Seconds 2
    Log-Step "  LanmanServer reiniciado."
} catch {
    Log-Step "  [WARN] Reinicio LanmanServer: $($_.Exception.Message)"
}

# [3.5/6] Habilitar acceso de invitado al servidor SMB. Es imprescindible en Win10/11:
# por defecto la cuenta Guest esta DESHABILITADA y "Everyone" en el share no incluye
# a guests no autenticados, asi que las cajas adicionales que se conectan sin credenciales
# reciben "Acceso denegado" aun con el share creado y la red OK. Modelo Eleventa: el share
# es publico en LAN cerrada, sin pedir password al usuario final.
Log-Step "[3.5/6] Habilitando acceso de invitado a SMB..."
try {
    # Identificar la cuenta Guest por SID localizado (en español es 'Invitado').
    $sidGuest = New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-546'  # BUILTIN\Guests
    $guestSid = New-Object System.Security.Principal.SecurityIdentifier ((Get-LocalUser | Where-Object SID -like 'S-1-5-21-*-501').SID.Value)
    $guestUser = Get-LocalUser | Where-Object { $_.SID.Value.EndsWith('-501') }
    if ($guestUser) {
        if (-not $guestUser.Enabled) {
            Enable-LocalUser -SID $guestUser.SID -ErrorAction SilentlyContinue
            Log-Step "  Cuenta Guest (SID $($guestUser.SID)) habilitada."
        } else {
            Log-Step "  Cuenta Guest ya estaba habilitada."
        }
    } else {
        Log-Step "  [WARN] No se encontro la cuenta Guest localmente."
    }

    # Permitir que Everyone (incluyendo guests anonimos) tenga aplicacion de permisos.
    $lsa = "HKLM:\SYSTEM\CurrentControlSet\Control\Lsa"
    New-ItemProperty -Path $lsa -Name "EveryoneIncludesAnonymous" -Value 1 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
    New-ItemProperty -Path $lsa -Name "RestrictAnonymous" -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
    New-ItemProperty -Path $lsa -Name "RestrictAnonymousSAM" -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null

    # LanmanServer: permitir null sessions sobre nuestro share concreto.
    $lan = "HKLM:\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters"
    New-ItemProperty -Path $lan -Name "RestrictNullSessAccess" -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
    try {
        $current = (Get-ItemProperty -Path $lan -Name "NullSessionShares" -ErrorAction SilentlyContinue).NullSessionShares
        if (-not $current) { $current = @() }
        if ($current -notcontains $shareName) {
            $new = @($current) + $shareName | Where-Object { $_ -ne $null -and $_ -ne '' }
            Set-ItemProperty -Path $lan -Name "NullSessionShares" -Value $new -Type MultiString -Force
            Log-Step "  '$shareName' agregado a NullSessionShares."
        }
    } catch { Log-Step "  [WARN] NullSessionShares: $($_.Exception.Message)" }

    Log-Step "  Acceso de invitado habilitado en LSA + LanmanServer."
} catch {
    Log-Step "  [WARN] Acceso invitado: $($_.Exception.Message)"
}

# [4/6] Permisos NTFS (BUILTIN\Users por SID, language-independent + usuario dedicado)
Log-Step "[4/6] Permisos NTFS..."
try {
    $acl = Get-Acl $dataFolder
    $sid = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-545")
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $sid,"Modify","ContainerInherit,ObjectInherit","None","Allow")
    $acl.SetAccessRule($rule)

    # También Modify para el usuario dedicado (las cajas adicionales lo usan).
    try {
        $ruleShareUser = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $machineShareUser,"Modify","ContainerInherit,ObjectInherit","None","Allow")
        $acl.SetAccessRule($ruleShareUser)
        Log-Step "  ACL Modify para $machineShareUser preparada."
    } catch {
        Log-Step "  [WARN] No se pudo añadir ACL para $machineShareUser : $($_.Exception.Message)"
    }

    Set-Acl -Path $dataFolder -AclObject $acl
    Log-Step "  ACL aplicada a BUILTIN\Users + $shareUser."
} catch {
    Log-Step "  [WARN] NTFS: $($_.Exception.Message)"
}

# [5/6] Firewall: SMB entrante TCP 445 (cajas adicionales) + API entrante TCP 7279 + Auto-discovery UDP 33279
Log-Step "[5/6] Firewall entrante SMB (TCP 445) + API (TCP 7279 + UDP 33279)..."
$null = Run-Net 'advfirewall firewall delete rule name="Grunflex POS SMB (445 in)"'
$okSmb = Run-Net 'advfirewall firewall add rule name="Grunflex POS SMB (445 in)" dir=in action=allow protocol=TCP localport=445 profile=any'
Log-Step "  Regla SMB TCP 445 creada: $okSmb"

$null = Run-Net 'advfirewall firewall delete rule name="Grunflex POS API (7279)"'
$ok1 = Run-Net 'advfirewall firewall add rule name="Grunflex POS API (7279)" dir=in action=allow protocol=TCP localport=7279 profile=any'
Log-Step "  Regla API TCP creada: $ok1"

$null = Run-Net 'advfirewall firewall delete rule name="Grunflex POS Discovery (33279)"'
$ok2 = Run-Net 'advfirewall firewall add rule name="Grunflex POS Discovery (33279)" dir=in action=allow protocol=UDP localport=33279 profile=any'
Log-Step "  Regla Discovery UDP creada: $ok2"

# [6/6] Registrar/actualizar servicio Windows
# El binario esta en {app}\API\GrunflexPOS.API.exe. La carpeta del script es {app}\ServerExtras.
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$apiExe    = Join-Path (Split-Path -Parent $scriptDir) "API\GrunflexPOS.API.exe"

Log-Step "[6/6] Servicio Windows '$serviceName' -> $apiExe"
if (-not (Test-Path $apiExe)) {
    Log-Step "  [ERROR] No se encontro el binario API; servicio no se registra."
} else {
    $existingSvc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($existingSvc) {
        try { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue } catch {}
        Start-Sleep -Milliseconds 500
        $null = Run-Sc "delete $serviceName"
        Start-Sleep -Milliseconds 800
    }

    # Crear servicio con binPath cita-escapada y auto-start
    $binArg = "binPath= ""\""$apiExe\""""  start= auto  DisplayName= ""Grunflex POS API"""
    $ok = Run-Sc "create $serviceName $binArg"
    Log-Step "  create: $ok"

    $null = Run-Sc "description $serviceName ""API HTTP de Grunflex POS. Permite multicaja, licencias, pagos."""

    # Recovery: 1er fallo restart 5s, 2do restart 10s, 3ro restart 30s. Ventana 24h.
    $null = Run-Sc "failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000"
    $null = Run-Sc "failureflag $serviceName 1"

    # Iniciar
    try {
        Start-Service -Name $serviceName -ErrorAction Stop
        Log-Step "  Servicio iniciado."
    } catch {
        Log-Step "  [WARN] No se pudo iniciar: $($_.Exception.Message)"
    }
}

Write-Host ""
Write-Host "==============================================="
Write-Host " Caja principal lista."
Write-Host "==============================================="
Log-Step "Configuracion finalizada."
exit 0
