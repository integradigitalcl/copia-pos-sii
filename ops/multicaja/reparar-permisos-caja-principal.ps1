# Repara permisos de %ProgramData%\GrunflexPOS para que el POS pueda crear el administrador.
# Ejecutar como Administrador (clic derecho en PowerShell).
#Requires -RunAsAdministrator

$ErrorActionPreference = "Stop"
$base = Join-Path $env:ProgramData "GrunflexPOS"

if (-not (Test-Path $base)) {
    Write-Host "No existe $base — instale primero como Caja principal."
    exit 1
}

foreach ($sub in @("data", "config", "logs")) {
    $path = Join-Path $base $sub
    if (-not (Test-Path $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
    Write-Host "ACL: $path"
    icacls $path /grant "*S-1-5-32-545:(OI)(CI)M" /grant "*S-1-5-11:(OI)(CI)M" /T /C | Out-Host
}

$data = Join-Path $base "data"
Get-ChildItem $data -Filter "*.db*" -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.IsReadOnly) { $_.IsReadOnly = $false }
}

$probe = Join-Path $data ".write-test"
"ok" | Set-Content -Path $probe -Encoding ASCII
Remove-Item $probe -Force
Write-Host ""
Write-Host "Listo. Abra Grunflex POS y cree el administrador de nuevo."
