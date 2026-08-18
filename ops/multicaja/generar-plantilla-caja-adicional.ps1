# Genera grunflex-terminal.json en la caja principal y lo copia a otra PC (opción B).
param(
    [string] $ServerIp = "192.168.1.7",
    [string] $MachineName = "",
    [string] $DestinoRemoto = "",
    [string] $UsuarioRemoto = "",
    [string] $PasswordRemoto = ""
)

$ErrorActionPreference = "Stop"
$apiBase = "http://$($ServerIp.Trim().TrimEnd('/')):7279/"

Write-Host "Servidor: $apiBase"
Invoke-RestMethod ($apiBase + "health/live") -TimeoutSec 8 | Out-Null
Write-Host "health/live: OK"

$machineAdicional = $env:COMPUTERNAME
if (-not [string]::IsNullOrWhiteSpace($MachineName)) {
    $machineAdicional = $MachineName.Trim()
} elseif (-not [string]::IsNullOrWhiteSpace($DestinoRemoto)) {
    if ($DestinoRemoto -match '^\d+\.\d+\.\d+\.\d+$') {
        try {
            $nb = nbtstat -A $DestinoRemoto 2>$null | Select-String '<00>\s+Unico'
            if ($nb -match '^\s*([^\s<]+)') { $machineAdicional = $Matches[1].Trim() }
        } catch { }
    } else {
        $machineAdicional = ($DestinoRemoto -replace '^\\\\','').Split('\')[0].Split('.')[0]
    }
}

$body = @{ machineName = $machineAdicional } | ConvertTo-Json
$reg = Invoke-RestMethod -Method Post -Uri ($apiBase + "api/multicaja/cajas/auto-registro") `
    -Body $body -ContentType "application/json" -TimeoutSec 20

if (-not $reg.ok) {
    throw "Auto-registro fallo: $($reg.error)"
}

$cajaId = $reg.cajaId.ToString()
Write-Host "Caja en servidor: $cajaId - $($reg.nombre)"

$shadow = "Data Source=$env:LOCALAPPDATA\GrunflexPOS\data\terminal_shadow.db;Cache=Shared"
$doc = @{
    ConnectionStrings = @{ Default = $shadow }
    Api = @{
        BaseUrl = $apiBase
        PagoBaseUrl = ($apiBase.TrimEnd('/') + "/api/pago")
    }
    CajaId = $cajaId
    TerminalRole = "client"
    Multicaja = @{
        UseApiOnlyClient = $true
        CatalogSyncIntervalSeconds = 5
        RequireSharedSecret = $false
        SharedSecret = ""
    }
    SmbShareUser = "GrunflexLan"
    SmbSharePassword = "GrunflexLan2025SMB"
}
$json = $doc | ConvertTo-Json -Depth 6

$desktop = [Environment]::GetFolderPath("Desktop")
if ([string]::IsNullOrWhiteSpace($desktop)) {
    $desktop = Join-Path $env:USERPROFILE "Desktop"
}
$rutaPrincipal = Join-Path $desktop "grunflex-terminal.json"
Set-Content -Path $rutaPrincipal -Value $json -Encoding UTF8
Write-Host "Plantilla en principal: $rutaPrincipal"

$destinosLocales = @(
    "$env:ProgramData\GrunflexPOS\config\grunflex-terminal.json",
    (Join-Path $env:ProgramFiles "Grunflex POS\grunflex-terminal.json")
)
foreach ($d in $destinosLocales) {
    try {
        $dir = Split-Path $d -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Copy-Item -LiteralPath $rutaPrincipal -Destination $d -Force
        Write-Host "Copia local: $d"
    } catch {
        Write-Host "Omitido $d : $($_.Exception.Message)"
    }
}

if ([string]::IsNullOrWhiteSpace($DestinoRemoto)) {
    Write-Host "`nListo. Copie manualmente $rutaPrincipal a la caja adicional."
    exit 0
}

$hostRemoto = $DestinoRemoto.Trim().TrimStart('\')
if ($hostRemoto -match '^\d+\.\d+\.\d+\.\d+$') {
    $hostRemoto = $machineAdicional
}
if ($hostRemoto -notmatch '^\\\\') {
    $hostRemoto = "\\$hostRemoto"
}
$copied = $false
$targets = @(
    (Join-Path $hostRemoto "c`$\Users\Public\Desktop\grunflex-terminal.json"),
    (Join-Path $hostRemoto "Users\Public\Desktop\grunflex-terminal.json")
)
foreach ($target in $targets) {
    try {
        $dir = Split-Path $target -Parent
        if (-not (Test-Path $dir)) { continue }
        Copy-Item -LiteralPath $rutaPrincipal -Destination $target -Force
        Write-Host "Copiado a: $target"
        $copied = $true
        break
    } catch {
        Write-Host "No se pudo copiar a $target : $($_.Exception.Message)"
    }
}
if (-not $copied) {
    $shareOrigen = "\\$env:COMPUTERNAME\GrunflexPOS-plantilla"
    try {
        if (-not (Test-Path $shareOrigen)) {
            New-Item -ItemType Directory -Path "C:\ProgramData\GrunflexPOS\plantilla-share" -Force | Out-Null
            Copy-Item -LiteralPath $rutaPrincipal -Destination "C:\ProgramData\GrunflexPOS\plantilla-share\grunflex-terminal.json" -Force
            net share GrunflexPOS-plantilla="C:\ProgramData\GrunflexPOS\plantilla-share" /GRANT:Everyone,READ 2>$null | Out-Null
        }
        Write-Host "Carpeta compartida en principal: $shareOrigen"
        Write-Host "En la adicional (DESKTOP-3I4K47K), copie desde Explorador:"
        Write-Host "  $shareOrigen\grunflex-terminal.json"
        Write-Host "  -> Escritorio"
    } catch {
        Write-Host "No se pudo crear recurso compartido: $($_.Exception.Message)"
    }
}

if (-not $copied) {
    Write-Host "`nNo se pudo copiar por red a $hostRemoto. Use USB o TeamViewer con:"
    Write-Host "  $rutaPrincipal"
} else {
    Write-Host "`nEn la caja adicional: cierre Grunflex POS y vuelva a abrirlo."
}
