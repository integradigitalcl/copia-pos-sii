param(
    [Parameter(Mandatory = $true)]
    [string]$AppRoot,

    [switch]$SkipBootstrap
)

$ErrorActionPreference = "Stop"
$AppRoot = (Resolve-Path $AppRoot).Path
$logPath = Join-Path $env:ProgramData "GrunflexPOS\logs\install-server-ready.log"

function Write-Step([string]$Message) {
    Write-Host $Message
    try {
        $dir = Split-Path $logPath -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        "$(Get-Date -Format s) $Message" | Add-Content -Path $logPath -Encoding UTF8
    } catch {
        # best effort
    }
}

function Grant-ModifyAcl {
    param([string[]]$Paths)
    foreach ($path in $Paths) {
        if (-not (Test-Path $path)) {
            New-Item -ItemType Directory -Force -Path $path | Out-Null
        }
        Write-Step "ACL: $path"
        icacls $path /grant "*S-1-5-32-545:(OI)(CI)M" /grant "*S-1-5-11:(OI)(CI)M" /T /C | Out-Null
    }
}

function Test-WriteAccess {
    param([string]$Directory)
    New-Item -ItemType Directory -Force -Path $Directory | Out-Null
    $probe = Join-Path $Directory ".write-test"
    try {
        "ok" | Set-Content -Path $probe -Encoding ASCII
        Remove-Item $probe -Force
        return $true
    } catch {
        return $false
    }
}

function Wait-ApiLive {
    param([int]$Seconds = 60)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:7279/health/live" -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
                return $true
            }
        } catch {
            Start-Sleep -Milliseconds 750
        }
    }
    return $false
}

function Ensure-ApiServiceRunning {
    $svc = Get-Service -Name "GrunflexPOSAPI" -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        Write-Step "WARN: servicio GrunflexPOSAPI no instalado."
        return $false
    }
    if ($svc.Status -ne "Running") {
        Write-Step "Iniciando servicio GrunflexPOSAPI..."
        Start-Service -Name "GrunflexPOSAPI" -ErrorAction Stop
        Start-Sleep -Seconds 2
    }
    return $true
}

Write-Step "=== Verificacion caja principal ==="

$programDataRoot = Join-Path $env:ProgramData "GrunflexPOS"
$localDataRoot = Join-Path $env:LOCALAPPDATA "GrunflexPOS"
Grant-ModifyAcl @(
    $programDataRoot,
    (Join-Path $programDataRoot "data"),
    (Join-Path $programDataRoot "config"),
    (Join-Path $programDataRoot "logs"),
    $localDataRoot
)

if (-not (Test-WriteAccess $localDataRoot)) {
    throw "No se puede escribir en $localDataRoot. Ejecute el instalador como administrador o repare permisos."
}

if (-not (Ensure-ApiServiceRunning)) {
    throw "Servicio GrunflexPOSAPI no instalado. Reinstale como caja principal."
}

Write-Step "Esperando API multicaja en :7279..."
if (-not (Wait-ApiLive -Seconds 60)) {
    throw "La API multicaja no respondio en http://127.0.0.1:7279/health/live dentro de 60 segundos."
}
Write-Step "API multicaja OK."

if (-not $SkipBootstrap) {
    $webExe = Join-Path $AppRoot "web\GrunflexPOS.Web.exe"
    $webDll = Join-Path $AppRoot "web\GrunflexPOS.Web.dll"
    if (Test-Path $webExe) {
        Write-Step "Preparando base local del POS Web..."
        # WinExe no siempre actualiza $LASTEXITCODE; hay que leer ExitCode del proceso.
        $bootstrap = Start-Process -FilePath $webExe -ArgumentList "--bootstrap-first-run" `
            -WorkingDirectory (Split-Path $webExe -Parent) -Wait -PassThru -WindowStyle Hidden
        if ($null -eq $bootstrap -or $bootstrap.ExitCode -ne 0) {
            $code = if ($null -eq $bootstrap) { "n/a" } else { $bootstrap.ExitCode }
            throw "No se pudo preparar la base local del POS Web (codigo $code)."
        }
        Write-Step "Base local del POS Web lista."
    } elseif (Test-Path $webDll) {
        Write-Step "Preparando base local del POS Web (dotnet)..."
        $dotnet = Get-Command dotnet -ErrorAction Stop
        $bootstrap = Start-Process -FilePath $dotnet.Source -ArgumentList @($webDll, "--bootstrap-first-run") `
            -WorkingDirectory (Split-Path $webDll -Parent) -Wait -PassThru -WindowStyle Hidden
        if ($null -eq $bootstrap -or $bootstrap.ExitCode -ne 0) {
            $code = if ($null -eq $bootstrap) { "n/a" } else { $bootstrap.ExitCode }
            throw "No se pudo preparar la base local del POS Web (codigo $code)."
        }
        Write-Step "Base local del POS Web lista."
    } else {
        Write-Step "WARN: no se encontro GrunflexPOS.Web para bootstrap."
    }
}

Write-Step "Caja principal verificada."
exit 0
