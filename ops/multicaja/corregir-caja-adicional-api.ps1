# Corrige caja adicional que quedó con Api en 127.0.0.1 (localhost).
param(
    [Parameter(Mandatory = $true)]
    [string] $ServerIp
)

$ErrorActionPreference = "Stop"
$hostClean = $ServerIp.Trim().TrimEnd('/')
if ($hostClean -match '^https?://') {
    if (-not [Uri]::TryCreate($hostClean, [UriKind]::Absolute, [ref]$null)) { throw "URL inválida" }
    $apiBase = if ($hostClean.EndsWith('/')) { $hostClean } else { "$hostClean/" }
    $pagoBase = $apiBase.TrimEnd('/') + "/api/pago"
} else {
    $apiBase = "http://${hostClean}:7279/"
    $pagoBase = "http://${hostClean}:7279/api/pago"
}

$shadow = "Data Source=$env:LOCALAPPDATA\GrunflexPOS\data\terminal_shadow.db;Cache=Shared"
$doc = @{
    ConnectionStrings = @{ Default = $shadow }
    Api = @{ BaseUrl = $apiBase; PagoBaseUrl = $pagoBase }
    CajaId = ""
    TerminalRole = "client"
    Multicaja = @{
        UseApiOnlyClient = $true
        RequireSharedSecret = $false
        SharedSecret = ""
    }
}
$json = $doc | ConvertTo-Json -Depth 6

$paths = @(
    "$env:ProgramData\GrunflexPOS\config\appsettings.local.json",
    "$env:LOCALAPPDATA\GrunflexPOS\config\appsettings.local.json"
)
foreach ($p in $paths) {
    $dir = Split-Path $p -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Set-Content -Path $p -Value $json -Encoding UTF8
    Write-Host "OK: $p"
}

$posEdgeCfg = "$env:ProgramData\PosEdge\config"
if (-not (Test-Path $posEdgeCfg)) { New-Item -ItemType Directory -Path $posEdgeCfg -Force | Out-Null }
Set-Content "$posEdgeCfg\configured-server-host.txt" $hostClean -Encoding UTF8
Set-Content "$posEdgeCfg\role.txt" "terminal" -Encoding UTF8
Set-Content "$posEdgeCfg\cached-server.txt" $apiBase -Encoding UTF8
Write-Host "OK: PosEdge config (IP $hostClean)"

Write-Host "`nProbando servidor..."
try {
    Invoke-RestMethod ($apiBase + "health/live") -TimeoutSec 8 | Out-Null
    Write-Host "health/live: OK" -ForegroundColor Green
} catch {
    Write-Host "health/live: FALLO - $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Encienda la caja principal y el servicio GrunflexPOSAPI antes de abrir el POS."
    exit 1
}

Write-Host "`nRegistrando caja en el servidor (auto-registro)..."
$cajaId = ""
try {
    $body = @{ machineName = $env:COMPUTERNAME } | ConvertTo-Json
    $reg = Invoke-RestMethod -Method Post -Uri ($apiBase + "api/multicaja/cajas/auto-registro") `
        -Body $body -ContentType "application/json" -TimeoutSec 15
    if ($reg.ok -and $reg.cajaId) {
        $cajaId = $reg.cajaId.ToString()
        Write-Host "CajaId: $cajaId ($($reg.nombre))" -ForegroundColor Green
        $doc.CajaId = $cajaId
        $json = $doc | ConvertTo-Json -Depth 6
        foreach ($p in $paths) { Set-Content -Path $p -Value $json -Encoding UTF8 }
    } else {
        Write-Host "Auto-registro no disponible: $($reg.error)" -ForegroundColor Yellow
        Write-Host "Copie grunflex-terminal.json desde la caja principal (Conectar mas cajas)."
    }
} catch {
    Write-Host "Auto-registro fallo: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host "Copie grunflex-terminal.json desde la caja principal (Conectar mas cajas)."
}

Write-Host "`nCierre Grunflex POS y vuelva a abrirlo."

$fwCandidates = @(
    (Join-Path ${env:ProgramFiles} 'PosEdge\SetupExtras\habilitar-firewall-multicaja.ps1'),
    (Join-Path (Split-Path $PSScriptRoot -Parent) '..\installer\habilitar-firewall-multicaja.ps1')
)
foreach ($fw in $fwCandidates) {
    if (Test-Path $fw) {
        Write-Host "`nAplicando firewall cliente (saliente 7279)..."
        & powershell -NoProfile -ExecutionPolicy Bypass -File $fw -Role Client -ServerIp $hostClean
        break
    }
}
