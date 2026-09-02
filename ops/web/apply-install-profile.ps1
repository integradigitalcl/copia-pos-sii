param(

    [Parameter(Mandatory = $true)]

    [ValidateSet("server", "client")]

    [string]$Role,



    [Parameter(Mandatory = $true)]

    [string]$AppRoot,



    [string]$ServerIp = "",

    [switch]$SkipDiscovery

)



$ErrorActionPreference = "Stop"

$AppRoot = (Resolve-Path $AppRoot).Path

$webSettings = Join-Path $AppRoot "web\appsettings.json"

$configDir = Join-Path $env:ProgramData "GrunflexPOS\config"

$roleMarker = Join-Path $configDir ".install-role"

$ensureClient = Join-Path $PSScriptRoot "ensure-multicaja-client.ps1"

if (-not (Test-Path $ensureClient)) {

    $ensureClient = Join-Path $AppRoot "ensure-multicaja-client.ps1"

}



function Set-WebMulticajaDefaults {

    param([string]$ApiBaseUrl)

    if (-not (Test-Path $webSettings)) {

        Write-Warning "No se encontró $webSettings"

        return

    }

    $json = Get-Content $webSettings -Raw | ConvertFrom-Json

    if (-not $json.Multicaja) { $json | Add-Member -NotePropertyName Multicaja -NotePropertyValue ([pscustomobject]@{}) }

    $json.Multicaja.Enabled = $true

    $json.Multicaja.ApiBaseUrl = $ApiBaseUrl

    if (-not $json.Licensing) { $json | Add-Member -NotePropertyName Licensing -NotePropertyValue ([pscustomobject]@{}) }

    $json.Licensing.ApiBaseUrl = $ApiBaseUrl

    $json | ConvertTo-Json -Depth 12 | Set-Content -Path $webSettings -Encoding UTF8

    Write-Host "Multicaja preconfigurada: $ApiBaseUrl"

}



function Get-SyncToolPath {

    $candidates = @(

        (Join-Path $AppRoot "Tools\SyncMulticajaSettings\SyncMulticajaSettings.exe"),

        (Join-Path $PSScriptRoot "SyncMulticajaSettings\bin\Release\net8.0\win-x64\publish\SyncMulticajaSettings.exe")

    )

    foreach ($path in $candidates) {

        if (Test-Path $path) { return $path }

    }

    $proj = Join-Path $PSScriptRoot "SyncMulticajaSettings\SyncMulticajaSettings.csproj"

    if (Test-Path $proj) { return $proj }

    return $null

}



function Invoke-SyncSettings {

    param([string]$ApiBaseUrl, [string]$TerminalRole)

    $webDb = Join-Path $env:LOCALAPPDATA "GrunflexPOS\grunflex-pos.db"

    $dbDir = Split-Path $webDb -Parent

    if (-not (Test-Path $dbDir)) {

        New-Item -ItemType Directory -Force -Path $dbDir | Out-Null

    }



    $tool = Get-SyncToolPath

    if (-not $tool) { return }



    if ($tool.EndsWith(".csproj")) {

        dotnet run --project $tool -- $webDb $ApiBaseUrl $TerminalRole | Out-Host

    } else {

        & $tool $webDb $ApiBaseUrl $TerminalRole | Out-Host

    }

}



New-Item -ItemType Directory -Force -Path $configDir | Out-Null
Set-Content -Path $roleMarker -Value $Role -Encoding ASCII

# Credenciales iniciales fijas (Bloc de notas las muestra al primer arranque).
$credShared = Join-Path $configDir "initial-admin-credentials.txt"
$credLocalDir = Join-Path $env:LOCALAPPDATA "GrunflexPOS"
$credLocal = Join-Path $credLocalDir "initial-admin-credentials.txt"
$credText = @"
Grunflex POS Web — credenciales iniciales
Generado: $(Get-Date -Format 'yyyy-MM-dd HH:mm')
Equipo: $env:COMPUTERNAME

Usuario: admin
Contraseña: 12345678

Cambie esta contraseña en el primer ingreso.
Elimine este archivo después de anotar las credenciales.
"@
New-Item -ItemType Directory -Force -Path $credLocalDir | Out-Null
Set-Content -Path $credShared -Value $credText -Encoding UTF8
Set-Content -Path $credLocal -Value $credText -Encoding UTF8
Write-Host "Credenciales iniciales escritas: admin / 12345678"
Start-Process -FilePath "$env:SystemRoot\System32\notepad.exe" -ArgumentList "`"$credShared`""

if ($Role -eq "server") {

    $setup = Join-Path $AppRoot "ServerExtras\habilitar-recurso-grunflexpos.ps1"

    if (-not (Test-Path $setup)) { throw "Falta $setup (instalación de caja principal incompleta)." }

    & $setup

    $ensureReady = Join-Path $PSScriptRoot "ensure-server-ready.ps1"
    if (-not (Test-Path $ensureReady)) {
        $ensureReady = Join-Path $AppRoot "ensure-server-ready.ps1"
    }
    if (-not (Test-Path $ensureReady)) {
        throw "Falta ensure-server-ready.ps1 en la instalación."
    }
    & $ensureReady -AppRoot $AppRoot

    Set-WebMulticajaDefaults -ApiBaseUrl "http://127.0.0.1:7279/"

    Invoke-SyncSettings -ApiBaseUrl "http://127.0.0.1:7279/" -TerminalRole "server"

    $fw = Join-Path $AppRoot "habilitar-firewall-multicaja.ps1"

    if (Test-Path $fw) { & $fw -Role Server }

    Write-Host "Perfil de caja principal aplicado."

    exit 0

}



# client — descubrimiento automático con reintentos y verificación

if (-not (Test-Path $ensureClient)) {

    throw "Falta ensure-multicaja-client.ps1 en la instalación."

}



$clientArgs = @{
    AppRoot = $AppRoot
    MaxAttempts = 10
    AttemptDelaySec = 3
    RequireConnection = $true
}

if ($ServerIp) { $clientArgs.ServerIp = $ServerIp }

if ($SkipDiscovery -and $ServerIp) { $clientArgs.MaxAttempts = 1 }



& $ensureClient @clientArgs



$fwClient = Join-Path $AppRoot "habilitar-firewall-multicaja.ps1"

if (Test-Path $fwClient) {

    $hostMarker = Join-Path $configDir ".multicaja-installer-host"

    $fwArgs = @{ Role = "Client" }

    if (Test-Path $hostMarker) {

        $fwArgs.ServerIp = (Get-Content $hostMarker -Raw).Trim()

    } elseif ($ServerIp) {

        $fwArgs.ServerIp = $ServerIp.Trim()

    }

    & $fwClient @fwArgs

}



Write-Host "Perfil de caja adicional aplicado."

exit 0

