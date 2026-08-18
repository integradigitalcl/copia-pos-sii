# Reparación completa caja principal: ruta BD + permisos + usuario admin.
# Ejecutar como administrador.
#Requires -RunAsAdministrator

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path

Write-Host "=== 1) Corregir ruta en appsettings ==="
& (Join-Path $PSScriptRoot "corregir-ruta-bd-caja-principal.ps1")

Write-Host "`n=== 2) Permisos ProgramData ==="
& (Join-Path $PSScriptRoot "reparar-permisos-caja-principal.ps1")

Write-Host "`n=== 3) Detener API (libera SQLite) ==="
sc.exe stop GrunflexPOSAPI | Out-Null
Start-Sleep -Seconds 3

$db = Join-Path $env:ProgramData "GrunflexPOS\data\grunflex.db"
$seedProj = Join-Path $PSScriptRoot "SeedAdmin\SeedAdmin.csproj"
dotnet build $seedProj -c Release | Out-Null
$seedDll = Join-Path $PSScriptRoot "SeedAdmin\bin\Release\net8.0\SeedAdmin.dll"
dotnet $seedDll $db "claudio" "demo1234" "cajero N°1"

Write-Host "`n=== 4) Iniciar API ==="
sc.exe start GrunflexPOSAPI | Out-Null

Write-Host "`n=== LISTO ==="
Write-Host "Usuario: claudio"
Write-Host "Clave:   demo1234"
Write-Host "Abra Grunflex POS e inicie sesion (ya no deberia pedir crear administrador)."
