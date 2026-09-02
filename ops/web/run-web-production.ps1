param(
    [int]$Port = 7373,
    [int]$BridgePort = 7390,
    [string]$PublishDirectory = "",
    [string]$BridgeDirectory = "",
    [switch]$NoBrowser,
    [switch]$NoBridge,
    [switch]$ShowCredentialsHint
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$project = Join-Path $root "GrunflexPOS.Web/GrunflexPOS.Web.csproj"
$publish = if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    Join-Path $root "artifacts/web"
} else {
    $PublishDirectory
}
$url = "http://127.0.0.1:$Port"
$bridgeUrl = "http://127.0.0.1:$BridgePort"
$credentialsPath = Join-Path $env:LOCALAPPDATA "GrunflexPOS\initial-admin-credentials.txt"
$sharedCredentialsPath = Join-Path $env:ProgramData "GrunflexPOS\config\initial-admin-credentials.txt"

function Show-CredentialsHint {
    $opened = $false
    foreach ($path in @($sharedCredentialsPath, $credentialsPath)) {
        if (Test-Path $path) {
            Write-Host ""
            Write-Host "Credenciales iniciales del administrador ($path):" -ForegroundColor Yellow
            Get-Content $path | ForEach-Object { Write-Host $_ }
            Write-Host "Se abrira el Bloc de notas con este archivo." -ForegroundColor Yellow
            Start-Process -FilePath "$env:SystemRoot\System32\notepad.exe" -ArgumentList "`"$path`""
            $opened = $true
            break
        }
    }
    if (-not $opened) {
        Write-Host "No se encontro archivo de credenciales aun; se creara al primer arranque (admin / 12345678)." -ForegroundColor Yellow
    }
}

function Write-DefaultAdminCredentialsFile {
    $content = @"
Grunflex POS Web — credenciales iniciales
Generado: $(Get-Date -Format 'yyyy-MM-dd HH:mm')
Equipo: $env:COMPUTERNAME

Usuario: admin
Contraseña: 12345678

Cambie esta contraseña en el primer ingreso.
Elimine este archivo después de anotar las credenciales.
"@
    $sharedDir = Split-Path $sharedCredentialsPath -Parent
    $localDir = Split-Path $credentialsPath -Parent
    New-Item -ItemType Directory -Force -Path $sharedDir, $localDir | Out-Null
    Set-Content -Path $sharedCredentialsPath -Value $content -Encoding UTF8
    Set-Content -Path $credentialsPath -Value $content -Encoding UTF8
}

function Get-BridgeToken {
    $tokenPath = Join-Path $env:LOCALAPPDATA "GrunflexPOS\HardwareBridge\token.dpapi"
    if (-not (Test-Path $tokenPath)) { return $null }
    Add-Type -AssemblyName System.Security
    $entropy = [Text.Encoding]::UTF8.GetBytes("GrunflexPOS.HardwareBridge.v1")
    $protected = [Convert]::FromBase64String((Get-Content -LiteralPath $tokenPath -Raw).Trim())
    $clear = [Security.Cryptography.ProtectedData]::Unprotect(
        $protected, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    return [Text.Encoding]::UTF8.GetString($clear)
}

function Wait-HttpOk {
    param([string]$Uri, [hashtable]$Headers = @{}, [int]$Seconds = 20)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Uri -Headers $Headers -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) { return $true }
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    return $false
}

function Start-HiddenProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory = ""
    )

    $startArgs = @{
        FilePath = $FilePath
        WindowStyle = "Hidden"
    }
    if ($ArgumentList.Count -gt 0) { $startArgs.ArgumentList = $ArgumentList }
    if ($WorkingDirectory) { $startArgs.WorkingDirectory = $WorkingDirectory }
    Start-Process @startArgs
}

function Start-WebProcess {
    param([string]$WorkingDir, [string]$Urls)
    $env:ASPNETCORE_DETAILEDERRORS = "true"
    $env:DetailedErrors = "true"
    $webExe = Join-Path $WorkingDir "GrunflexPOS.Web.exe"
    $webDll = Join-Path $WorkingDir "GrunflexPOS.Web.dll"
    if (Test-Path $webExe) {
        Start-HiddenProcess -FilePath $webExe -ArgumentList @("--urls", $Urls, "--environment", "Production") -WorkingDirectory $WorkingDir
        return
    }
    if (Test-Path $webDll) {
        Start-HiddenProcess -FilePath "dotnet" -ArgumentList @($webDll, "--urls", $Urls, "--environment", "Production") -WorkingDirectory $WorkingDir
        return
    }
    throw "No se encontró GrunflexPOS.Web.exe ni GrunflexPOS.Web.dll en $WorkingDir"
}

function Ensure-ApiService {
    $svc = Get-Service -Name "GrunflexPOSAPI" -ErrorAction SilentlyContinue
    if ($null -ne $svc -and $svc.Status -eq "Running") {
        if (Wait-HttpOk -Uri "http://127.0.0.1:7279/health/live" -Seconds 5) {
            Write-Host "API multicaja OK en http://127.0.0.1:7279"
            return $true
        }
    }
    if ($null -ne $svc -and $svc.Status -ne "Running") {
        Write-Host "Iniciando servicio API multicaja (GrunflexPOSAPI)..."
        try {
            Start-Service -Name "GrunflexPOSAPI" -ErrorAction Stop
            if (Wait-HttpOk -Uri "http://127.0.0.1:7279/health/live" -Seconds 30) {
                Write-Host "API multicaja OK en http://127.0.0.1:7279"
                return $true
            }
        } catch {
            Write-Warning "No se pudo iniciar GrunflexPOSAPI: $($_.Exception.Message)"
        }
    }

    $apiCandidates = @(
        (Join-Path $PSScriptRoot "API\GrunflexPOS.API.exe"),
        (Join-Path (Split-Path $PSScriptRoot -Parent) "API\GrunflexPOS.API.exe"),
        (Join-Path $env:ProgramFiles "GrunflexPOS Web\API\GrunflexPOS.API.exe")
    )
    foreach ($apiExe in $apiCandidates) {
        if (-not (Test-Path $apiExe)) { continue }
        $apiDir = Split-Path $apiExe -Parent
        $running = Get-Process -Name "GrunflexPOS.API" -ErrorAction SilentlyContinue
        if ($running) {
            if (Wait-HttpOk -Uri "http://127.0.0.1:7279/health/live" -Seconds 10) { return $true }
        }
        Write-Host "Iniciando API multicaja en proceso: $apiExe"
        Start-HiddenProcess -FilePath $apiExe -ArgumentList @("--urls", "http://127.0.0.1:7279") -WorkingDirectory $apiDir
        if (Wait-HttpOk -Uri "http://127.0.0.1:7279/health/live" -Seconds 30) {
            Write-Host "API multicaja OK en http://127.0.0.1:7279"
            return $true
        }
        Write-Warning "La API se lanzó pero /health/live no respondió a tiempo."
    }
    return $false
}

function Bootstrap-WebDatabase {
    param([string]$WorkingDir)
    $webExe = Join-Path $WorkingDir "GrunflexPOS.Web.exe"
    $webDll = Join-Path $WorkingDir "GrunflexPOS.Web.dll"
    if (Test-Path $webExe) {
        & $webExe --bootstrap-first-run
        return $LASTEXITCODE -eq 0
    }
    if (Test-Path $webDll) {
        dotnet $webDll --bootstrap-first-run
        return $LASTEXITCODE -eq 0
    }
    return $false
}

if (-not (Test-Path (Join-Path $publish "GrunflexPOS.Web.dll")) -and -not (Test-Path (Join-Path $publish "GrunflexPOS.Web.exe"))) {
    Write-Host "Publicando GrunflexPOS.Web (Release, self-contained)..."
    New-Item -ItemType Directory -Force -Path $publish | Out-Null
    dotnet publish $project --configuration Release -r win-x64 --self-contained true --output $publish
    if ($LASTEXITCODE -ne 0) { throw "Falló publish de GrunflexPOS.Web." }
}

$installRolePath = Join-Path $env:ProgramData "GrunflexPOS\config\.install-role"
$installRole = if (Test-Path $installRolePath) { (Get-Content $installRolePath -Raw).Trim() } else { "" }
$isClientInstall = $installRole -eq "client"

if (-not $isClientInstall) {
    $apiOk = Ensure-ApiService
    if (-not $apiOk) {
        Write-Warning "La API multicaja no respondió antes de abrir el POS. El login puede fallar hasta que GrunflexPOSAPI esté activo."
    }
}

$appRoot = if (Test-Path (Join-Path $PSScriptRoot "web\GrunflexPOS.Web.dll")) {
    $PSScriptRoot
} elseif (Test-Path (Join-Path $PSScriptRoot "web\GrunflexPOS.Web.exe")) {
    $PSScriptRoot
} else {
    Split-Path $publish -Parent
}

if ($isClientInstall) {
    $ensureClient = Join-Path $PSScriptRoot "ensure-multicaja-client.ps1"
    if (-not (Test-Path $ensureClient)) {
        $ensureClient = Join-Path $appRoot "ensure-multicaja-client.ps1"
    }
    if (Test-Path $ensureClient) {
        Write-Host "Verificando conexión con la caja principal..."
        try {
            & $ensureClient -AppRoot $appRoot -MaxAttempts 3 -AttemptDelaySec 2 -StartupMode
        } catch {
            Write-Warning "No se pudo verificar la caja principal: $($_.Exception.Message). Iniciando POS de todos modos."
        }
    }
}

if (-not $NoBridge) {
    if ([string]::IsNullOrWhiteSpace($BridgeDirectory)) {
        $bridgeCandidates = @(
            (Join-Path $PSScriptRoot "bridge"),
            (Join-Path (Split-Path $publish -Parent) "bridge"),
            (Join-Path $root "artifacts\web-installer\bridge"),
            (Join-Path $env:LOCALAPPDATA "GrunflexPOS\HardwareBridge")
        )
        foreach ($candidate in $bridgeCandidates) {
            if ((Test-Path (Join-Path $candidate "GrunflexPOS.HardwareBridge.dll")) -or
                (Test-Path (Join-Path $candidate "GrunflexPOS.HardwareBridge.exe"))) {
                $BridgeDirectory = $candidate
                break
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($BridgeDirectory)) {
        $bridgeExe = Join-Path $BridgeDirectory "GrunflexPOS.HardwareBridge.exe"
        $bridgeDll = Join-Path $BridgeDirectory "GrunflexPOS.HardwareBridge.dll"
        Write-Host "Iniciando Hardware Bridge desde $BridgeDirectory"
        if (Test-Path $bridgeExe) {
            Start-HiddenProcess -FilePath $bridgeExe -WorkingDirectory $BridgeDirectory
        } else {
            Start-HiddenProcess -FilePath "dotnet" -ArgumentList @($bridgeDll) -WorkingDirectory $BridgeDirectory
        }

        Start-Sleep -Seconds 1
        $token = $null
        for ($i = 0; $i -lt 20 -and -not $token; $i++) {
            Start-Sleep -Milliseconds 400
            try { $token = Get-BridgeToken } catch { $token = $null }
        }

        if ($token) {
            $ok = Wait-HttpOk -Uri "$bridgeUrl/health" -Headers @{ Authorization = "Bearer $token" } -Seconds 15
            if ($ok) { Write-Host "Hardware Bridge OK en $bridgeUrl" }
            else { Write-Warning "Hardware Bridge no respondió a tiempo en $bridgeUrl" }
        } else {
            Write-Warning "No se pudo leer token DPAPI del bridge; continúe y verifique :$BridgePort"
        }
    } else {
        Write-Warning "No se encontró Hardware Bridge. Use -BridgeDirectory o publique con compile-web-installer.ps1 -PublishOnly"
    }
}

Write-Host "Iniciando Grunflex POS Web en $url"
if (-not (Test-Path $sharedCredentialsPath) -and -not (Test-Path $credentialsPath)) {
    Write-DefaultAdminCredentialsFile
}
if (-not $isClientInstall) {
    if (-not (Bootstrap-WebDatabase -WorkingDir $publish)) {
        Write-Warning "No se pudo preparar la base local del POS Web antes del arranque."
    }
}
Start-WebProcess -WorkingDir $publish -Urls $url

if (-not (Wait-HttpOk -Uri "$url/health" -Seconds 25)) {
    Write-Warning "La web no respondió /health a tiempo. Revise la consola de GrunflexPOS.Web."
} else {
    Write-Host "Web OK en $url"
}

# Dar tiempo a que el POS cree el usuario admin si era primera vez
Start-Sleep -Seconds 2
if (-not (Test-Path $sharedCredentialsPath) -and -not (Test-Path $credentialsPath)) {
    Write-DefaultAdminCredentialsFile
}

if ($ShowCredentialsHint -or (Test-Path $sharedCredentialsPath) -or (Test-Path $credentialsPath)) {
    Show-CredentialsHint
}

if (-not $NoBrowser) {
    Start-Process $url
}
