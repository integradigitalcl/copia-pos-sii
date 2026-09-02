param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [string]$IsccPath = "",
    [string]$PublicKeyPemPath = "",
    [switch]$PublishOnly
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$webProject = Join-Path $root "GrunflexPOS.Web/GrunflexPOS.Web.csproj"
$bridgeProject = Join-Path $root "GrunflexPOS.HardwareBridge/GrunflexPOS.HardwareBridge.csproj"
$apiProject = Join-Path $root "GrunflexPOS.API/GrunflexPOS.API.csproj"
$staging = Join-Path $root "artifacts/web-installer"
$webOut = Join-Path $staging "web"
$bridgeOut = Join-Path $staging "bridge"
$apiOut = Join-Path $staging "API"
$extrasOut = Join-Path $staging "ServerExtras"
$toolsOut = Join-Path $staging "Tools\SeedAdmin"
$iss = Join-Path $PSScriptRoot "GrunflexPOS-Web.iss"
$installerExtras = Join-Path $PSScriptRoot "..\installer"

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $webOut, $bridgeOut, $apiOut, $extrasOut | Out-Null

Write-Host "Publicando web ($Configuration, win-x64, self-contained)..."
dotnet publish $webProject -c $Configuration -r win-x64 --self-contained true -o $webOut
if ($LASTEXITCODE -ne 0) { throw "Falló publish de GrunflexPOS.Web." }

if (Test-Path $bridgeProject) {
    Write-Host "Publicando Hardware Bridge ($Configuration, win-x64, self-contained)..."
    dotnet publish $bridgeProject -c $Configuration -r win-x64 --self-contained true -o $bridgeOut
    if ($LASTEXITCODE -ne 0) { throw "Falló publish de Hardware Bridge." }
}

Write-Host "Publicando API multicaja ($Configuration, win-x64)..."
dotnet publish $apiProject -c $Configuration -r win-x64 --self-contained true -o $apiOut
if ($LASTEXITCODE -ne 0) { throw "Falló publish de GrunflexPOS.API." }

$seedProject = Join-Path $root "ops/multicaja/SeedAdmin/SeedAdmin.csproj"
$syncProject = Join-Path $PSScriptRoot "SyncMulticajaSettings/SyncMulticajaSettings.csproj"
$syncOut = Join-Path $staging "Tools\SyncMulticajaSettings"
if (Test-Path $seedProject) {
    Write-Host "Publicando SeedAdmin ($Configuration, win-x64)..."
    New-Item -ItemType Directory -Force -Path $toolsOut | Out-Null
    dotnet publish $seedProject -c $Configuration -r win-x64 --self-contained true -o $toolsOut
    if ($LASTEXITCODE -ne 0) { throw "Falló publish de SeedAdmin." }
}
if (Test-Path $syncProject) {
    Write-Host "Publicando SyncMulticajaSettings ($Configuration, win-x64)..."
    New-Item -ItemType Directory -Force -Path $syncOut | Out-Null
    dotnet publish $syncProject -c $Configuration -r win-x64 --self-contained true -o $syncOut
    if ($LASTEXITCODE -ne 0) { throw "Falló publish de SyncMulticajaSettings." }
}

$extrasSrc = Join-Path $installerExtras "ServerExtras"
if (-not (Test-Path $extrasSrc)) { throw "No se encontró $extrasSrc" }
Copy-Item -Path (Join-Path $extrasSrc "*") -Destination $extrasOut -Recurse -Force
Copy-Item (Join-Path $installerExtras "habilitar-firewall-multicaja.ps1") $staging -Force
Copy-Item (Join-Path $PSScriptRoot "run-web-production.ps1") $staging -Force
Copy-Item (Join-Path $PSScriptRoot "verify-golive.ps1") $staging -Force
Copy-Item (Join-Path $PSScriptRoot "apply-install-profile.ps1") $staging -Force
Copy-Item (Join-Path $PSScriptRoot "ensure-server-ready.ps1") $staging -Force
Copy-Item (Join-Path $PSScriptRoot "ensure-multicaja-client.ps1") $staging -Force
Copy-Item (Join-Path $PSScriptRoot "discover-multicaja-server.ps1") $staging -Force
Copy-Item (Join-Path $PSScriptRoot "reset-multicaja-admin.ps1") $staging -Force
$posIcon = Join-Path $root "ops\posedgesetup\assets\grunflex-pos.ico"
if (-not (Test-Path $posIcon)) {
    $posIcon = Join-Path $root "GrunflexPOS2\app.ico"
}
if (-not (Test-Path $posIcon)) {
    throw "No se encontró grunflex-pos.ico / app.ico para el icono del instalador."
}
Copy-Item $posIcon (Join-Path $staging "grunflex-pos.ico") -Force
Write-Host "Icono POS copiado a staging."

foreach ($path in @(
    (Join-Path $apiOut "appsettings.local.json"),
    (Join-Path $apiOut "appsettings.Development.json")
)) {
    if (Test-Path $path) { Remove-Item $path -Force }
}

function Set-AllowDemoOff([string]$settingsPath) {
    if (-not (Test-Path $settingsPath)) { return }
    $raw = Get-Content $settingsPath -Raw
    if ($raw -match '"AllowDemoCredentials"\s*:\s*true') {
        $raw = $raw -replace '"AllowDemoCredentials"\s*:\s*true', '"AllowDemoCredentials": false'
        Set-Content -Path $settingsPath -Value $raw -Encoding UTF8
        Write-Host "Forzado Security:AllowDemoCredentials=false en $(Split-Path $settingsPath -Leaf)."
    }
}
Set-AllowDemoOff (Join-Path $webOut "appsettings.json")

if (-not $PublicKeyPemPath) {
    $PublicKeyPemPath = Join-Path $env:LOCALAPPDATA "GrunflexPOS\data\licensing-public.pem"
}
if (Test-Path $PublicKeyPemPath) {
    $pem = (Get-Content $PublicKeyPemPath -Raw).Trim()
    if ($pem) {
        $jsonPem = ConvertTo-Json $pem -Compress
        foreach ($tgt in @(
            (Join-Path $webOut "appsettings.json"),
            (Join-Path $apiOut "appsettings.json")
        )) {
            if (-not (Test-Path $tgt)) { continue }
            $content = Get-Content $tgt -Raw
            if ($content -match '"Licensing"\s*:\s*\{') {
                if ($content -match '"PublicKeyPem"\s*:\s*"[^"]*"') {
                    $content = [regex]::Replace($content, '"PublicKeyPem"\s*:\s*"[^"]*"', '"PublicKeyPem": ' + $jsonPem)
                } else {
                    $content = [regex]::Replace($content, '("Licensing"\s*:\s*\{)', '$1' + "`n    " + '"PublicKeyPem": ' + $jsonPem + ',')
                }
            }
            Set-Content -Path $tgt -Value $content -Encoding UTF8
        }
        Write-Host "Clave pública RSA insertada en web y API."
    }
} else {
    Write-Warning "No se encontró licensing-public.pem; licencias firmadas no validarán hasta insertar la clave."
}

if ($PublishOnly) {
    Write-Host "PublishOnly: artefactos en $staging"
    Write-Host "  web    -> $webOut"
    Write-Host "  bridge -> $bridgeOut"
    Write-Host "  API    -> $apiOut"
    exit 0
}

$iscc = if ($IsccPath) { $IsccPath } else {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) {
    Write-Warning "No se encontró ISCC.exe. Use -PublishOnly para validar publish sin instalador."
    exit 2
}

& $iscc "/DStaging=$staging" "/DConfiguration=$Configuration" $iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup terminó con código $LASTEXITCODE." }
Write-Host "Instalador web listo en $PSScriptRoot\out"
