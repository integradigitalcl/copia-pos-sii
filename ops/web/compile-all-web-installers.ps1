param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$WindowsOnly,
    [switch]$MacOnly
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$desktopDir = Join-Path ([Environment]::GetFolderPath("Desktop")) "GrunflexPOS-Web-Instalador"
New-Item -ItemType Directory -Force -Path $desktopDir | Out-Null

if (-not $MacOnly) {
    Write-Host "=== Compilando instalador Windows ===" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "compile-web-installer.ps1") -Configuration $Configuration
    $winSetup = Join-Path $PSScriptRoot "out\GrunflexPOS-Web-Setup.exe"
    if (Test-Path $winSetup) {
        Copy-Item $winSetup (Join-Path $desktopDir "GrunflexPOS-Web-Setup.exe") -Force
        Write-Host "Copiado a $desktopDir\GrunflexPOS-Web-Setup.exe" -ForegroundColor Green
    }
}

if (-not $WindowsOnly) {
    Write-Host "=== Compilando paquete macOS Catalina ===" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "compile-macos-installer.ps1") -Configuration $Configuration
    $macZip = Join-Path $PSScriptRoot "out\GrunflexPOS-Web-macOS-Catalina.zip"
    if (Test-Path $macZip) {
        Copy-Item $macZip (Join-Path $desktopDir "GrunflexPOS-Web-macOS-Catalina.zip") -Force
        Write-Host "Copiado a $desktopDir\GrunflexPOS-Web-macOS-Catalina.zip" -ForegroundColor Green
    }
    & (Join-Path $PSScriptRoot "compile-macos-dmg.ps1") -OutputDir $desktopDir
}

Write-Host "Instaladores listos en: $desktopDir" -ForegroundColor Yellow
