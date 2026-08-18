# Prueba de carga multicaja: N ventas independientes (1 unidad c/u), alternando cajas.
param(
    [string] $BaseUrl = "http://127.0.0.1:7279",
    [int] $Ventas = 300,
    [string] $Db = "",
    [string] $CodigoBarras = "",
    [string] $CajaPrincipalNombre = "CAJA-PRINCIPAL-STRESS",
    [string] $CajaAdicionalNombre = "CAJA-ADICIONAL-STRESS",
    [string] $SharedSecret = "",
    [switch] $SoloPrincipal,
    [switch] $PrepararStock
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($Db)) {
    $Db = Join-Path $env:ProgramData "GrunflexPOS\data\grunflex.db"
}

function New-McHeaders {
    param([string]$CajaId = "")
    $h = @{ Accept = "application/json" }
    if ($SharedSecret) { $h["X-Grunflex-Multicaja-Key"] = $SharedSecret }
    if ($CajaId) { $h["X-Grunflex-Caja-Id"] = $CajaId }
    $h["X-Grunflex-Terminal"] = $env:COMPUTERNAME
    return $h
}

function Invoke-Mc {
    param(
        [string]$Method = "Get",
        [string]$Path,
        $Body = $null,
        [hashtable]$ExtraHeaders = @{}
    )
    $uri = "$($BaseUrl.TrimEnd('/'))/$($Path.TrimStart('/'))"
    $headers = New-McHeaders
    foreach ($k in $ExtraHeaders.Keys) { $headers[$k] = $ExtraHeaders[$k] }
    if ($null -eq $Body) {
        return Invoke-RestMethod -Uri $uri -Method $Method -Headers $headers -TimeoutSec 120
    }
    return Invoke-RestMethod -Uri $uri -Method $Method -Headers $headers `
        -Body ($Body | ConvertTo-Json -Depth 8 -Compress) -ContentType "application/json" -TimeoutSec 120
}

function Get-OrOpen-Session {
    param([guid]$CajaId, [guid]$UserId, [string]$Username)
    try {
        return Invoke-Mc -Path "api/multicaja/caja-sesiones/abierta?cajaId=$CajaId"
    } catch {
        return Invoke-Mc -Method Post -Path "api/multicaja/caja-sesiones/abrir" -Body @{
            cajaId = $CajaId.ToString("D")
            usuarioId = $UserId.ToString("D")
            username = $Username
            montoInicial = 0
        }
    }
}

function Register-Caja {
    param([string]$MachineName)
    $r = Invoke-Mc -Method Post -Path "api/multicaja/cajas/auto-registro" -Body @{ machineName = $MachineName }
    if (-not $r.ok) { throw "auto-registro falló para $MachineName : $($r | ConvertTo-Json -Compress)" }
    return $r
}

Write-Host "=== Prueba $Ventas ventas multicaja (1 producto c/u) ===" -ForegroundColor Cyan
Write-Host "API: $BaseUrl"
Write-Host "BD:  $Db"

# Health
try { Invoke-Mc -Path "health/live" | Out-Null } catch {
    Write-Host "FAIL: API no responde en $BaseUrl ($($_.Exception.Message))" -ForegroundColor Red
    exit 10
}
Write-Host "OK  health/live"

if ($PrepararStock -and (Test-Path $Db)) {
    $seedProj = Join-Path $repo "ops\multicaja\SeedProductoPrueba\SeedProductoPrueba.csproj"
    dotnet build $seedProj -c Release | Out-Null
    $seedDll = Join-Path $repo "ops\multicaja\SeedProductoPrueba\bin\Release\net8.0\SeedProductoPrueba.dll"
    dotnet $seedDll $Db | Out-Null
    Write-Host "OK  seed TEST-MC001 (stock base 100; se ampliará vía API si hace falta)"
}

$users = Invoke-Mc -Path "api/multicaja/usuarios"
if ($users.Count -lt 1) { Write-Host "FAIL: sin usuarios"; exit 11 }
$u = $users[0]
$login = Invoke-Mc -Method Post -Path "api/multicaja/login" -Body @{
    username = $u.username; password = $u.password
}
if (-not $login.ok) { Write-Host "FAIL login"; exit 12 }
$userId = [guid]$login.id
Write-Host "OK  login $($u.username)"

$prods = Invoke-Mc -Path "api/multicaja/productos"
if ($prods.Count -lt 1) { Write-Host "FAIL: catálogo vacío"; exit 13 }
$prod = if ($CodigoBarras) {
    $prods | Where-Object { $_.codigoBarras -eq $CodigoBarras } | Select-Object -First 1
} else {
    $prods | Where-Object { $_.codigoBarras -eq "TEST-MC001" } | Select-Object -First 1
}
if (-not $prod) { $prod = $prods | Sort-Object -Property stock -Descending | Select-Object -First 1 }
$stockInicial = [int]$prod.stock
Write-Host "OK  producto=$($prod.nombre) codigo=$($prod.codigoBarras) stock=$stockInicial precio=$($prod.precio)"

$cajaP = Register-Caja -MachineName $CajaPrincipalNombre
$cajaPId = [guid]$cajaP.cajaId
$sesP = Get-OrOpen-Session -CajaId $cajaPId -UserId $userId -Username $u.username
Write-Host "OK  caja principal $($cajaP.nombre) sesion=$($sesP.id)"

if ($stockInicial -lt $Ventas) {
    if (-not $PrepararStock) {
        Write-Host "FAIL: stock insuficiente ($stockInicial < $Ventas). Use -PrepararStock para reponer vía API." -ForegroundColor Red
        exit 14
    }
    $delta = ($Ventas + 50) - $stockInicial
    Write-Host "Repone stock +$delta unidades vía API..."
    $aj = Invoke-Mc -Method Post -Path "api/multicaja/inventario/ajustar" -Body @{
        requestId = [Guid]::NewGuid().ToString("N")
        cajaId = $cajaPId.ToString("D")
        cajaSesionId = $sesP.id
        usuarioId = $userId.ToString("D")
        productoId = [int]$prod.id
        cantidadDelta = $delta
        motivo = "Preparación stress $Ventas ventas"
    }
    if (-not $aj.ok) {
        Write-Host "FAIL ajuste stock: $($aj.error)" -ForegroundColor Red
        exit 15
    }
    $stockInicial = [int]$aj.stockNuevo
    $prodId = [int]$prod.id
    $allProds = Invoke-Mc -Path "api/multicaja/productos"
    $prod = $allProds | Where-Object { $_.id -eq $prodId } | Select-Object -First 1
    Write-Host "OK  stock repuesto -> $stockInicial"
}

$cajas = @(@{
    label = "principal"
    cajaId = $cajaPId
    sesionId = [guid]$sesP.id
})

if (-not $SoloPrincipal) {
    $cajaA = Register-Caja -MachineName $CajaAdicionalNombre
    $cajaAId = [guid]$cajaA.cajaId
    $sesA = Get-OrOpen-Session -CajaId $cajaAId -UserId $userId -Username $u.username
    Write-Host "OK  caja adicional $($cajaA.nombre) sesion=$($sesA.id)"
    $cajas += @{
        label = "adicional"
        cajaId = $cajaAId
        sesionId = [guid]$sesA.id
    }
}

$ok = 0
$fail = 0
$errores = New-Object System.Collections.Generic.List[string]
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$tickets = New-Object System.Collections.Generic.List[int]

for ($i = 1; $i -le $Ventas; $i++) {
    $caja = $cajas[($i - 1) % $cajas.Count]
    $body = @{
        requestId = [Guid]::NewGuid().ToString("N")
        cajaId = $caja.cajaId.ToString("D")
        cajaSesionId = $caja.sesionId.ToString("D")
        usuarioId = $userId.ToString("D")
        cliente = "Stress MC #$i"
        metodoPago = "Efectivo"
        esConsumoPersonal = $false
        items = @(@{
            codigoBarras = $prod.codigoBarras
            producto = $prod.nombre
            cantidad = 1
            precio = [decimal]$prod.precio
        })
    }
    try {
        $r = Invoke-Mc -Method Post -Path "api/multicaja/ventas/commit" -Body $body `
            -ExtraHeaders @{ "X-Grunflex-Caja-Id" = $caja.cajaId.ToString("D") }
        if ($r.ok) {
            $ok++
            [void]$tickets.Add([int]$r.numeroTicket)
        } else {
            $fail++
            [void]$errores.Add("#$i $($caja.label): $($r.errorCode) $($r.error)")
        }
    } catch {
        $fail++
        [void]$errores.Add("#$i $($caja.label): $($_.Exception.Message)")
    }
    if ($i % 50 -eq 0) {
        Write-Host "  ... $i / $Ventas (ok=$ok fail=$fail)" -ForegroundColor DarkGray
    }
}
$sw.Stop()

Start-Sleep -Milliseconds 400
$prodsFinal = Invoke-Mc -Path "api/multicaja/productos"
$prodIdFinal = [int]$prod.id
$pf = $prodsFinal | Where-Object { $_.id -eq $prodIdFinal } | Select-Object -First 1
$stockFinal = [int]$pf.stock
$esperado = $stockInicial - $Ventas

Write-Host ""
Write-Host "=== Resultado ===" -ForegroundColor Cyan
Write-Host "Ventas OK:     $ok / $Ventas"
Write-Host "Ventas FAIL:   $fail"
Write-Host "Duración:      $([math]::Round($sw.Elapsed.TotalSeconds, 1)) s ($([math]::Round($ok / [math]::Max($sw.Elapsed.TotalSeconds, 0.001), 1)) ventas/s)"
Write-Host "Tickets:       $($tickets[0]) .. $($tickets[-1]) (únicos=$($tickets | Select-Object -Unique | Measure-Object | Select-Object -ExpandProperty Count))"
Write-Host "Stock:         $stockInicial -> $stockFinal (esperado $esperado)"

if ($errores.Count -gt 0) {
    Write-Host "Primeros errores:" -ForegroundColor Yellow
    $errores | Select-Object -First 5 | ForEach-Object { Write-Host "  $_" }
}

if ($fail -gt 0 -or $stockFinal -ne $esperado) {
    Write-Host "FAIL: prueba incompleta o stock inconsistente" -ForegroundColor Red
    exit 20
}

Write-Host "PASS: $Ventas ventas separadas multicaja OK" -ForegroundColor Green
exit 0
