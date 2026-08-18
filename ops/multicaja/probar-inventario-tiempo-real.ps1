# Prueba inventario tiempo real contra API en produccion (principal).
param(
    [string] $BaseUrl = "http://127.0.0.1:7279",
    [string] $Db = "",
    [string] $ActivationId = "GF-20260524-6036"
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($Db)) {
    $Db = Join-Path $env:ProgramData "GrunflexPOS\data\grunflex.db"
}

Write-Host "=== Prueba inventario tiempo real ===" -ForegroundColor Cyan
Write-Host "API: $BaseUrl | BD: $Db"

# 1) Articulo ficticio
$seedProj = Join-Path $repo "ops\multicaja\SeedProductoPrueba\SeedProductoPrueba.csproj"
dotnet build $seedProj -c Release | Out-Null
$seedDll = Join-Path $repo "ops\multicaja\SeedProductoPrueba\bin\Release\net8.0\SeedProductoPrueba.dll"
dotnet $seedDll $Db
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 2) Catalogo API
$prods = Invoke-RestMethod "$BaseUrl/api/multicaja/productos" -TimeoutSec 10
if ($prods.Count -lt 1) {
    Write-Host "FAIL: catalogo API vacio tras seed" -ForegroundColor Red
    exit 11
}
$prod = $prods | Where-Object { $_.codigoBarras -eq "TEST-MC001" } | Select-Object -First 1
if (-not $prod) { $prod = $prods[0] }
$stockInicial = [int]$prod.stock
Write-Host "OK producto=$($prod.nombre) stock_inicial=$stockInicial codigo=$($prod.codigoBarras)"

# 3) Login + caja + sesion
$users = Invoke-RestMethod "$BaseUrl/api/multicaja/usuarios"
$u = $users[0]
$login = Invoke-RestMethod "$BaseUrl/api/multicaja/login" -Method Post `
    -Body (@{ username = $u.username; password = $u.password } | ConvertTo-Json) `
    -ContentType "application/json"
if (-not $login.ok) { Write-Host "FAIL login"; exit 12 }
$userId = $login.id

$cajaResp = Invoke-RestMethod "$BaseUrl/api/multicaja/cajas/auto-registro" -Method Post `
    -Body (@{ MachineName = $env:COMPUTERNAME } | ConvertTo-Json) `
    -ContentType "application/json"
$cajaId = $cajaResp.cajaId

$sesPayload = @{ cajaId = $cajaId; usuarioId = $userId; username = $u.username; montoInicial = 0 } | ConvertTo-Json
try {
    $ses = Invoke-RestMethod "$BaseUrl/api/multicaja/caja-sesiones/abrir" -Method Post `
        -Body $sesPayload -ContentType "application/json"
} catch {
    $ses = Invoke-RestMethod "$BaseUrl/api/multicaja/caja-sesiones/abierta?cajaId=$cajaId" -TimeoutSec 10
    if (-not $ses) { throw "No se pudo abrir ni obtener sesion abierta para caja $cajaId" }
}
$sesionId = $ses.id
Write-Host "OK sesion=$sesionId caja=$($cajaResp.nombre)"

# 4) Venta -2 unidades (API central)
$ventaBody = @{
    requestId = [Guid]::NewGuid().ToString("N")
    cajaId = $cajaId
    cajaSesionId = $sesionId
    usuarioId = $userId
    cliente = "Prueba inventario E2E"
    metodoPago = "Efectivo"
    esConsumoPersonal = $false
    items = @(@{
        codigoBarras = $prod.codigoBarras
        producto = $prod.nombre
        cantidad = 2
        precio = [decimal]$prod.precio
    })
} | ConvertTo-Json -Depth 5
$venta = Invoke-RestMethod "$BaseUrl/api/multicaja/ventas/commit" -Method Post `
    -Body $ventaBody -ContentType "application/json"
if (-not $venta.ok) {
    Write-Host "FAIL venta: $($venta.errorCode) $($venta.error)" -ForegroundColor Red
    exit 13
}
Write-Host "OK venta ticket=$($venta.numeroTicket) (-2 unidades)"

# 5) sync/changes debe registrar inventario
Start-Sleep -Milliseconds 300
$sync = Invoke-RestMethod "$BaseUrl/api/multicaja/sync/changes?cursor=0&limit=20&domains=inventory,products"
$invChanges = @($sync.changes | Where-Object { $_.domain -in @("inventory","products") })
if ($invChanges.Count -lt 1) {
    Write-Host "FAIL sync/changes sin eventos de inventario" -ForegroundColor Red
    exit 14
}
Write-Host "OK sync/changes: $($invChanges.Count) evento(s) inventario/productos"

# 6) Poll catalogo <3s (simula 2 terminales leyendo sombra via pull)
$esperado = $stockInicial - 2
$deadline = (Get-Date).AddSeconds(3)
$stockVisto = $stockInicial
$okTiempo = $false
while ((Get-Date) -lt $deadline) {
    $poll = Invoke-RestMethod "$BaseUrl/api/multicaja/productos" -TimeoutSec 5
    $p = $poll | Where-Object { $_.id -eq $prod.id } | Select-Object -First 1
    if ($p) { $stockVisto = [int]$p.stock }
    if ($stockVisto -eq $esperado) { $okTiempo = $true; break }
    Start-Sleep -Milliseconds 150
}

if (-not $okTiempo) {
    Write-Host "FAIL stock tras venta: esperado $esperado, visto $stockVisto (>3s)" -ForegroundColor Red
    exit 15
}
Write-Host "OK stock en catalogo en <3s: $stockInicial -> $stockVisto" -ForegroundColor Green

# 7) Ajuste inventario +1 via API
$ajuste = Invoke-RestMethod "$BaseUrl/api/multicaja/inventario/ajustar" -Method Post `
    -Body (@{
        requestId = [Guid]::NewGuid().ToString("N")
        cajaId = $cajaId
        cajaSesionId = $sesionId
        usuarioId = $userId
        productoId = [int]$prod.id
        cantidadDelta = 1
        motivo = "Prueba ajuste E2E"
    } | ConvertTo-Json) `
    -ContentType "application/json"
if (-not $ajuste.ok) {
    Write-Host "FAIL ajuste: $($ajuste.error)" -ForegroundColor Red
    exit 16
}
Write-Host "OK ajuste API: $($ajuste.stockAnterior) -> $($ajuste.stockNuevo)"

Write-Host "`n=== INVENTARIO TIEMPO REAL: OK ===" -ForegroundColor Green
Write-Host "Venta, sync/changes, stock visible <3s y ajuste central funcionan."
