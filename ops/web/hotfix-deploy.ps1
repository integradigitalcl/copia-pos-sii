param(
    [string]$Source = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path "publish/web-hotfix"),
    [string]$Target = (Join-Path ${env:ProgramFiles} "GrunflexPOS Web/web")
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path (Join-Path $Source "GrunflexPOS.Web.dll"))) {
    throw "No existe $Source\GrunflexPOS.Web.dll. Ejecute dotnet publish primero."
}
if (-not (Test-Path $Target)) {
    throw "No existe la carpeta de instalación: $Target"
}

Get-Process -Name "GrunflexPOS.Web" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Copy-Item -Path (Join-Path $Source "GrunflexPOS.Web.exe") -Destination (Join-Path $Target "GrunflexPOS.Web.exe") -Force
Copy-Item -Path (Join-Path $Source "GrunflexPOS.Web.dll") -Destination (Join-Path $Target "GrunflexPOS.Web.dll") -Force
Copy-Item -Path (Join-Path $Source "Grunflex.Licensing.Abstractions.dll") -Destination (Join-Path $Target "Grunflex.Licensing.Abstractions.dll") -Force
Copy-Item -Path (Join-Path $Source "wwwroot/reports-parity.css") -Destination (Join-Path $Target "wwwroot/reports-parity.css") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $Source "wwwroot/configuration-parity.css") -Destination (Join-Path $Target "wwwroot/configuration-parity.css") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $Source "wwwroot/ticket-boleta.css") -Destination (Join-Path $Target "wwwroot/ticket-boleta.css") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $Source "wwwroot/sidebar-buttons.css") -Destination (Join-Path $Target "wwwroot/sidebar-buttons.css") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $Source "wwwroot/sidebar-parity.css") -Destination (Join-Path $Target "wwwroot/sidebar-parity.css") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $Source "wwwroot/pos-override.css") -Destination (Join-Path $Target "wwwroot/pos-override.css") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $Source "wwwroot/app.css") -Destination (Join-Path $Target "wwwroot/app.css") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $Source "wwwroot/checkout-flow.css") -Destination (Join-Path $Target "wwwroot/checkout-flow.css") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $Source "wwwroot/products-parity.css") -Destination (Join-Path $Target "wwwroot/products-parity.css") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $Source "wwwroot/keyboard-shortcuts.js") -Destination (Join-Path $Target "wwwroot/keyboard-shortcuts.js") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $Source "wwwroot/GrunflexPOS.Web.styles.css") -Destination (Join-Path $Target "wwwroot/GrunflexPOS.Web.styles.css") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $Source "wwwroot/assets/logo_custom.png") -Destination (Join-Path $Target "wwwroot/assets/logo_custom.png") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $Source "wwwroot/assets/logo.png") -Destination (Join-Path $Target "wwwroot/assets/logo.png") -Force -ErrorAction SilentlyContinue

$bridgeSource = Join-Path (Split-Path $Source -Parent) "bridge-hotfix"
$bridgeTarget = Join-Path (Split-Path $Target -Parent) "bridge"
if (-not (Test-Path $bridgeSource)) {
    $bridgeSource = Join-Path (Split-Path (Split-Path $Source -Parent) -Parent) "publish/bridge-hotfix"
}
if (Test-Path (Join-Path $bridgeSource "GrunflexPOS.HardwareBridge.dll")) {
    Get-Process -Name "GrunflexPOS.HardwareBridge" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1
    Copy-Item -Path (Join-Path $bridgeSource "GrunflexPOS.HardwareBridge.exe") -Destination (Join-Path $bridgeTarget "GrunflexPOS.HardwareBridge.exe") -Force
    Copy-Item -Path (Join-Path $bridgeSource "GrunflexPOS.HardwareBridge.dll") -Destination (Join-Path $bridgeTarget "GrunflexPOS.HardwareBridge.dll") -Force
    Start-Process -FilePath (Join-Path $bridgeTarget "GrunflexPOS.HardwareBridge.exe") -WorkingDirectory $bridgeTarget -WindowStyle Hidden
}

Start-Process -FilePath (Join-Path $Target "GrunflexPOS.Web.exe") -WorkingDirectory $Target -WindowStyle Hidden
Write-Host "Hotfix desplegado en $Target"
