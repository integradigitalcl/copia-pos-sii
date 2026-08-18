# Repara firewall multicaja según rol local (principal o adicional). Requiere admin.
param(
    [string] $ServerIp = "",
    [ValidateSet('Server', 'Client', 'Auto')]
    [string] $Role = 'Auto'
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..\..')).Path
$fw = Join-Path ${env:ProgramFiles} 'PosEdge\SetupExtras\habilitar-firewall-multicaja.ps1'
if (-not (Test-Path $fw)) {
    $fw = Join-Path $repoRoot 'ops\installer\habilitar-firewall-multicaja.ps1'
}
if (-not (Test-Path $fw)) { throw "No se encontró habilitar-firewall-multicaja.ps1" }

if ($Role -eq 'Auto') {
    $roleFile = Join-Path $env:ProgramData 'PosEdge\config\role.txt'
    $txt = if (Test-Path $roleFile) { (Get-Content $roleFile -Raw).Trim().ToLowerInvariant() } else { 'server' }
    $Role = if ($txt -eq 'terminal' -or $txt -eq 'client') { 'Client' } else { 'Server' }
}

Write-Host "Reparando firewall multicaja (rol=$Role)..." -ForegroundColor Cyan
$args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $fw, '-Role', $Role)
if ($Role -eq 'Client' -and -not [string]::IsNullOrWhiteSpace($ServerIp)) {
    & powershell @args -ServerIp $ServerIp
}
else {
    & powershell @args
}
exit $LASTEXITCODE
