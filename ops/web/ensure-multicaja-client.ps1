param(
    [Parameter(Mandatory = $true)]
    [string]$AppRoot,

    [string]$ServerIp = "",
    [int]$MaxAttempts = 8,
    [int]$AttemptDelaySec = 4,
    [switch]$StartupMode,
    [switch]$RequireConnection
)

$ErrorActionPreference = "Stop"
$AppRoot = (Resolve-Path $AppRoot).Path
$webSettings = Join-Path $AppRoot "web\appsettings.json"
$configDir = Join-Path $env:ProgramData "GrunflexPOS\config"
$roleMarker = Join-Path $configDir ".install-role"
$hostMarker = Join-Path $configDir ".multicaja-installer-host"
$runtimeConfig = Join-Path $configDir "multicaja-client.json"

function Ensure-TrailingSlash([string]$url) {
    if ([string]::IsNullOrWhiteSpace($url)) { return $url }
    if ($url.EndsWith("/")) { return $url }
    return "$url/"
}

function Resolve-ApiBaseUrl([string]$Raw) {
    if ([string]::IsNullOrWhiteSpace($Raw)) { return $null }
    $raw = $Raw.Trim().TrimEnd("/")
    if ($raw -match "^https?://") {
        return (Ensure-TrailingSlash $raw)
    }
    return "http://${raw}:7279/"
}

function Test-ApiLive([string]$apiBase) {
    if ([string]::IsNullOrWhiteSpace($apiBase)) { return $false }
    try {
        $health = (Ensure-TrailingSlash $apiBase) + "health/live"
        $resp = Invoke-WebRequest -Uri $health -UseBasicParsing -TimeoutSec 3
        return $resp.StatusCode -ge 200 -and $resp.StatusCode -lt 300
    } catch {
        return $false
    }
}

function Test-IsLoopbackUrl([string]$apiBase) {
    try {
        $hostName = ([uri](Ensure-TrailingSlash $apiBase)).Host
        return $hostName -in @("127.0.0.1", "localhost", "::1")
    } catch {
        return $false
    }
}

function Set-RuntimeMulticajaConfig([string]$ApiBaseUrl) {
    New-Item -ItemType Directory -Force -Path $configDir | Out-Null
    @{
        ApiBaseUrl = (Ensure-TrailingSlash $ApiBaseUrl)
        TerminalRole = "client"
        Enabled = $true
        UpdatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    } | ConvertTo-Json | Set-Content -Path $runtimeConfig -Encoding UTF8
}

function Set-WebMulticajaDefaults([string]$ApiBaseUrl) {
    Set-RuntimeMulticajaConfig -ApiBaseUrl $ApiBaseUrl

    if (-not (Test-Path $webSettings)) {
        Write-Warning "No se encontró $webSettings; se usó configuración en $runtimeConfig"
        return
    }

    try {
        $json = Get-Content $webSettings -Raw | ConvertFrom-Json
        if (-not $json.Multicaja) { $json | Add-Member -NotePropertyName Multicaja -NotePropertyValue ([pscustomobject]@{}) }
        $json.Multicaja.Enabled = $true
        $json.Multicaja.ApiBaseUrl = (Ensure-TrailingSlash $ApiBaseUrl)
        if (-not $json.Licensing) { $json | Add-Member -NotePropertyName Licensing -NotePropertyValue ([pscustomobject]@{}) }
        $json.Licensing.ApiBaseUrl = (Ensure-TrailingSlash $ApiBaseUrl)
        $json | ConvertTo-Json -Depth 12 | Set-Content -Path $webSettings -Encoding UTF8
        Write-Host "appsettings.json → $ApiBaseUrl"
    } catch {
        Write-Warning "No se pudo actualizar appsettings.json (permisos). Se guardó en $runtimeConfig"
    }
}

function Get-SyncToolPath {
    $candidates = @(
        (Join-Path $AppRoot "Tools\SyncMulticajaSettings\SyncMulticajaSettings.exe"),
        (Join-Path $PSScriptRoot "SyncMulticajaSettings\bin\Release\net8.0\win-x64\publish\SyncMulticajaSettings.exe"),
        (Join-Path $PSScriptRoot "SyncMulticajaSettings\bin\Release\net8.0\SyncMulticajaSettings.exe")
    )
    foreach ($path in $candidates) {
        if (Test-Path $path) { return $path }
    }
    $proj = Join-Path $PSScriptRoot "SyncMulticajaSettings\SyncMulticajaSettings.csproj"
    if (Test-Path $proj) { return $proj }
    return $null
}

function Invoke-SyncSettings([string]$ApiBaseUrl) {
    $webDb = Join-Path $env:LOCALAPPDATA "GrunflexPOS\grunflex-pos.db"
    $dbDir = Split-Path $webDb -Parent
    if (-not (Test-Path $dbDir)) {
        New-Item -ItemType Directory -Force -Path $dbDir | Out-Null
    }

    $tool = Get-SyncToolPath
    if (-not $tool) {
        Write-Warning "SyncMulticajaSettings no encontrado; solo configuración en runtime."
        return
    }

    if ($tool.EndsWith(".csproj")) {
        dotnet run --project $tool -- $webDb $ApiBaseUrl "client" | Out-Host
    } else {
        & $tool $webDb $ApiBaseUrl "client" | Out-Host
    }
}

function Find-MulticajaServer {
    $discover = Join-Path $AppRoot "discover-multicaja-server.ps1"
    if (-not (Test-Path $discover)) {
        $discover = Join-Path $PSScriptRoot "discover-multicaja-server.ps1"
    }
    if (-not (Test-Path $discover)) { return $null }
    $found = & $discover -TimeoutMs 8000 2>$null
    if ($LASTEXITCODE -eq 0 -and $found) {
        return (Resolve-ApiBaseUrl $found)
    }
    return $null
}

function Get-SavedApiBase {
    if (Test-Path $runtimeConfig) {
        try {
            $cfg = Get-Content $runtimeConfig -Raw | ConvertFrom-Json
            if ($cfg.ApiBaseUrl) {
                return (Resolve-ApiBaseUrl ([string]$cfg.ApiBaseUrl))
            }
        } catch { }
    }

    if (Test-Path $webSettings) {
        try {
            $json = Get-Content $webSettings -Raw | ConvertFrom-Json
            if ($json.Multicaja.ApiBaseUrl) {
                return (Resolve-ApiBaseUrl ([string]$json.Multicaja.ApiBaseUrl))
            }
        } catch { }
    }

    if (Test-Path $hostMarker) {
        return (Resolve-ApiBaseUrl (Get-Content $hostMarker -Raw).Trim())
    }

    return $null
}

New-Item -ItemType Directory -Force -Path $configDir | Out-Null
Set-Content -Path $roleMarker -Value "client" -Encoding ASCII

$apiBase = Resolve-ApiBaseUrl $ServerIp
if ($apiBase -and (Test-IsLoopbackUrl $apiBase)) {
    Write-Host "Se ignoró 127.0.0.1 como caja principal en modo cliente."
    $apiBase = $null
}

if ($apiBase -and -not (Test-ApiLive $apiBase)) {
    Write-Host "IP indicada no responde aún ($apiBase); se intentará descubrimiento automático..."
    $apiBase = $null
}

if (-not $apiBase) {
    $saved = Get-SavedApiBase
    if ($saved -and -not (Test-IsLoopbackUrl $saved) -and (Test-ApiLive $saved)) {
        $apiBase = $saved
        Write-Host "Usando caja principal guardada: $apiBase"
    }
}

for ($attempt = 1; $attempt -le $MaxAttempts -and -not $apiBase; $attempt++) {
    Write-Host "Buscando caja principal (intento $attempt/$MaxAttempts)..."
    $candidate = Find-MulticajaServer
    if ($candidate -and -not (Test-IsLoopbackUrl $candidate) -and (Test-ApiLive $candidate)) {
        $apiBase = $candidate
        Write-Host "Caja principal detectada: $apiBase"
        break
    }
    if ($attempt -lt $MaxAttempts) {
        Start-Sleep -Seconds $AttemptDelaySec
    }
}

if (-not $apiBase) {
    $saved = Get-SavedApiBase
    if ($saved -and -not (Test-IsLoopbackUrl $saved)) {
        $apiBase = $saved
        if ($StartupMode) {
            Write-Warning "La caja principal no responde ahora ($apiBase). El POS iniciará igual; reintente login cuando esté disponible."
        }
    }
}

if (-not $apiBase) {
    $message = "No se encontró la caja principal en la red. Verifique que la API esté activa en la caja principal y que ambas PCs estén en la misma red."
    if ($StartupMode -and -not $RequireConnection) {
        Write-Warning $message
        exit 0
    }
    throw $message
}

Set-WebMulticajaDefaults -ApiBaseUrl $apiBase
Set-Content -Path $hostMarker -Value ([uri]$apiBase).Host -Encoding ASCII
Invoke-SyncSettings -ApiBaseUrl $apiBase

if (-not (Test-ApiLive $apiBase)) {
    $message = "La caja principal no responde en $apiBase"
    if ($StartupMode -and -not $RequireConnection) {
        Write-Warning "$message. El POS iniciará igual."
        exit 0
    }
    throw $message
}

Write-Host "Conexión multicaja lista: $apiBase"
exit 0
