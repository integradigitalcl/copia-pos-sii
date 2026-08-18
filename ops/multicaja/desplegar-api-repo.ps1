# Despliega GrunflexPOS.API desde el repo e inicia servicio permanente.
# Con admin: copia a PosEdge\GrunflexApi + servicio Windows GrunflexPOSAPI.
# Sin admin: copia a %ProgramData%\GrunflexPOS\api + tarea al iniciar sesion.
param(
    [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"
$log = Join-Path $env:ProgramData "GrunflexPOS\logs\deploy-api.log"
$logDir = Split-Path $log -Parent
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$resultFile = Join-Path $env:ProgramData "GrunflexPOS\deploy-result.txt"
function Log([string]$m) {
    $line = "$(Get-Date -Format s) $m"
    Write-Host $m
    Add-Content -Path $log -Value $line -Encoding UTF8
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$posEdge = "${env:ProgramFiles}\PosEdge"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
$dataDir = Join-Path $env:ProgramData "GrunflexPOS\data"
$db = Join-Path $dataDir "grunflex.db"
$deploy = Join-Path $env:TEMP "grunflex-api-deploy-sc"
$apiDestAdmin = Join-Path $posEdge "GrunflexApi"
$apiDestUser = Join-Path $env:ProgramData "GrunflexPOS\api"
$apiDest = if ($isAdmin -and (Test-Path $posEdge)) { $apiDestAdmin } else { $apiDestUser }

Log "=== Despliegue API multicaja ==="
Log "Admin=$isAdmin Destino=$apiDest"

Log "[1/6] Compilando API..."
if (-not $SkipBuild) {
    if (Test-Path $deploy) { Remove-Item $deploy -Recurse -Force -ErrorAction SilentlyContinue }
    dotnet publish (Join-Path $repoRoot "GrunflexPOS.API\GrunflexPOS.API.csproj") `
        -c Release -r win-x64 --self-contained true -o $deploy | Out-Host
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Log "[2/6] Deteniendo API previa..."
Get-Service GrunflexPOSAPI -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Service GrunflexPOSAPI -Force -ErrorAction SilentlyContinue
}
Get-Process -Name "GrunflexPOS.API" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

Log "[3/6] Copiando binarios..."
New-Item -ItemType Directory -Path $apiDest -Force | Out-Null
robocopy $deploy $apiDest /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy fallo: $LASTEXITCODE" }

$startScript = Join-Path $env:ProgramData "GrunflexPOS\start-api.ps1"
$exe = Join-Path $apiDest "GrunflexPOS.API.exe"
@(
    '$ErrorActionPreference = "Stop"'
    '$dataDir = "' + $dataDir.Replace('\', '\\') + '"'
    '$db = "' + $db.Replace('\', '\\') + '"'
    '$env:GRUNFLEX_DATA_DIR = $dataDir'
    '$env:Multicaja__PosConnectionString = "Data Source=$db;Cache=Shared"'
    '$env:ConnectionStrings__Pos = "Data Source=$db;Cache=Shared"'
    '$env:ASPNETCORE_ENVIRONMENT = "Production"'
    '$env:DOTNET_ENVIRONMENT = "Production"'
    'Set-Location "' + $apiDest.Replace('\', '\\') + '"'
    '& "' + $exe.Replace('\', '\\') + '"'
) | Set-Content -Path $startScript -Encoding UTF8

if ($isAdmin -and (Test-Path (Join-Path $posEdge "SetupAgent\PosEdge.SetupAgent.exe"))) {
    Log "[4/6] Registrando servicio Windows (admin)..."
    & (Join-Path $posEdge "SetupAgent\PosEdge.SetupAgent.exe") --payload-root $posEdge --repair
    if ($LASTEXITCODE -ne 0) { Log "WARN SetupAgent exit=$LASTEXITCODE (instalando servicio directo)" }

    $apiExe = Join-Path $posEdge "GrunflexApi\GrunflexPOS.API.exe"
    if (-not (Get-Service GrunflexPOSAPI -ErrorAction SilentlyContinue) -and (Test-Path $apiExe)) {
        Log "Instalando GrunflexPOSAPI via sc.exe..."
        & sc.exe stop GrunflexPOSAPI 2>$null | Out-Null
        & sc.exe delete GrunflexPOSAPI 2>$null | Out-Null
        Start-Sleep -Seconds 1
        & sc.exe create GrunflexPOSAPI binPath= "`"$apiExe`"" start= auto DisplayName= "Grunflex POS API"
        & sc.exe description GrunflexPOSAPI "API HTTP Grunflex POS multicaja (puerto 7279)"
        & sc.exe failure GrunflexPOSAPI reset= 86400 actions= restart/5000/restart/10000/restart/30000
    }

    $regKey = "HKLM:\SYSTEM\CurrentControlSet\Services\GrunflexPOSAPI\Environment"
    New-Item -Path $regKey -Force | Out-Null
    Set-ItemProperty -Path $regKey -Name GRUNFLEX_DATA_DIR -Value $dataDir
    Set-ItemProperty -Path $regKey -Name Multicaja__PosConnectionString -Value "Data Source=$db;Cache=Shared"
    Set-ItemProperty -Path $regKey -Name ConnectionStrings__Pos -Value "Data Source=$db;Cache=Shared"
    Set-ItemProperty -Path $regKey -Name ASPNETCORE_ENVIRONMENT -Value "Production"
    Set-ItemProperty -Path $regKey -Name DOTNET_ENVIRONMENT -Value "Production"
    try {
        Set-Service -Name GrunflexPOSAPI -StartupType Automatic -ErrorAction SilentlyContinue
        Start-Service GrunflexPOSAPI -ErrorAction Stop
        Log "Servicio GrunflexPOSAPI iniciado."
    } catch { Log "WARN servicio: $($_.Exception.Message)" }
} else {
    Log "[4/6] Autostart al iniciar sesion (opcional)..."
    $taskName = "GrunflexPOSAPI"
    try {
        & schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null
        & schtasks.exe /Create /F /TN $taskName /TR "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$startScript`"" /SC ONLOGON /RL LIMITED 2>&1 | ForEach-Object { Log $_ }
        Log "Tarea '$taskName' registrada."
    } catch {
        Log "WARN schtasks: $($_.Exception.Message) (API se inicia solo en esta sesion)"
    }
    Log "[5/6] Iniciando API ahora..."
    Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$startScript`"" -WindowStyle Hidden
}

Log "[6/6] Verificando health..."
function Invoke-FirewallMulticaja([string] $fwRole) {
    $candidates = @(
        (Join-Path $posEdge "SetupExtras\habilitar-firewall-multicaja.ps1"),
        (Join-Path $repoRoot "ops\installer\habilitar-firewall-multicaja.ps1")
    )
    foreach ($fw in $candidates) {
        if (Test-Path $fw) {
            Log "Firewall multicaja ($fwRole): $fw"
            & powershell -NoProfile -ExecutionPolicy Bypass -File $fw -Role $fwRole
            return
        }
    }
    Log "WARN: habilitar-firewall-multicaja.ps1 no encontrado"
}

if ($isAdmin) {
    Invoke-FirewallMulticaja -fwRole Server
} else {
    Log "WARN firewall: ejecute ops\multicaja\reparar-firewall-multicaja.ps1 como admin si la LAN bloquea 7279"
}
$ok = $false
for ($i = 0; $i -lt 45; $i++) {
    try {
        Invoke-RestMethod "http://127.0.0.1:7279/health/live" -TimeoutSec 3 | Out-Null
        $ok = $true
        break
    } catch { Start-Sleep -Seconds 1 }
}
if (-not $ok) {
    Log "WARN servicio/proceso no respondio; intento arranque directo..."
    Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$startScript`"" -WindowStyle Hidden
    Start-Sleep -Seconds 3
    for ($i = 0; $i -lt 30; $i++) {
        try {
            Invoke-RestMethod "http://127.0.0.1:7279/health/live" -TimeoutSec 3 | Out-Null
            $ok = $true
            break
        } catch { Start-Sleep -Seconds 1 }
    }
}
if (-not $ok) {
    Log "FAIL: API no respondio en :7279"
    exit 10
}
Invoke-RestMethod "http://127.0.0.1:7279/health/ready" -TimeoutSec 10 | Out-Null
Log "OK health/live + health/ready"

$lan = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object {
    $_.IPAddress -notlike '127.*' -and $_.PrefixOrigin -ne 'WellKnown' -and $_.IPAddress -notlike '169.254*'
} | Where-Object { $_.IPAddress -like '192.168.*' } | Select-Object -First 1).IPAddress
if (-not $lan) {
    $lan = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' } | Select-Object -First 1).IPAddress
}
Log "=== verificar-conexion $lan ==="
$verif = Join-Path $posEdge "SetupExtras\verificar-conexion-multicaja.ps1"
if (-not (Test-Path $verif)) { $verif = Join-Path $repoRoot "ops\multicaja\verificar-conexion-multicaja.ps1" }
& powershell -NoProfile -ExecutionPolicy Bypass -File $verif -ServerIp $lan

Log "=== LISTO === API en $apiDest | log $log"
$svc = Get-Service GrunflexPOSAPI -ErrorAction SilentlyContinue
@(
    "finished=$(Get-Date -Format s)",
    "admin=$isAdmin",
    "dest=$apiDest",
    "service=$($svc.Status)",
    "log=$log"
) | Set-Content -Path $resultFile -Encoding UTF8
exit 0
