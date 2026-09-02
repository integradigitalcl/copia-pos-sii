param(
    [string]$WebUrl = "http://127.0.0.1:7373",
    [string]$BridgeUrl = "http://127.0.0.1:7390",
    [string]$PublishRoot = "",
    [switch]$SkipBridge,
    [switch]$SkipInvoice,
    [switch]$SkipTests
)

$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $root "artifacts/web-installer"
}

$failed = 0
$passed = 0

function Write-Check([bool]$Ok, [string]$Name, [string]$Detail = "") {
    if ($Ok) {
        $script:passed++
        Write-Host "[PASS] $Name $(if ($Detail) { "- $Detail" })" -ForegroundColor Green
    } else {
        $script:failed++
        Write-Host "[FAIL] $Name $(if ($Detail) { "- $Detail" })" -ForegroundColor Red
    }
}

function Get-BridgeToken {
    $tokenPath = Join-Path $env:LOCALAPPDATA "GrunflexPOS\HardwareBridge\token.dpapi"
    if (-not (Test-Path $tokenPath)) { return $null }
    Add-Type -AssemblyName System.Security
    $entropy = [Text.Encoding]::UTF8.GetBytes("GrunflexPOS.HardwareBridge.v1")
    $protected = [Convert]::FromBase64String((Get-Content -LiteralPath $tokenPath -Raw).Trim())
    $clear = [Security.Cryptography.ProtectedData]::Unprotect(
        $protected, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    return [Text.Encoding]::UTF8.GetString($clear)
}

Write-Host "=== Grunflex POS Web - verificacion go-live ===" -ForegroundColor Cyan
Write-Host "Web: $WebUrl"
Write-Host "Bridge: $BridgeUrl"
Write-Host ""

# 1) Publish artifacts
$webDll = Join-Path $PublishRoot "web\GrunflexPOS.Web.dll"
$bridgeDll = Join-Path $PublishRoot "bridge\GrunflexPOS.HardwareBridge.dll"
$apiDll = Join-Path $PublishRoot "API\GrunflexPOS.API.dll"
Write-Check (Test-Path $webDll) "Publish web" $webDll
Write-Check (Test-Path $bridgeDll) "Publish bridge" $bridgeDll
Write-Check (Test-Path $apiDll) "Publish API (caja principal)" $apiDll

# 2) Production security flag in staged appsettings
$prodSettings = Join-Path $PublishRoot "web\appsettings.json"
if (Test-Path $prodSettings) {
    $json = Get-Content $prodSettings -Raw | ConvertFrom-Json
    $allowDemo = $false
    if ($json.Security -and $null -ne $json.Security.AllowDemoCredentials) {
        $allowDemo = [bool]$json.Security.AllowDemoCredentials
    }
    Write-Check (-not $allowDemo) "AllowDemoCredentials=false en publish" "valor=$allowDemo"
} else {
    Write-Check $false "appsettings.json en publish" "no encontrado"
}

# 3) Web health
try {
    $health = Invoke-RestMethod "$WebUrl/health" -TimeoutSec 5
    Write-Check ($health.status -eq "ok") "GET $WebUrl/health" ($health | ConvertTo-Json -Compress)
} catch {
    Write-Check $false "GET $WebUrl/health" $_.Exception.Message
}

# 4) Invoice mock receiver
if (-not $SkipInvoice) {
    try {
        $inv = Invoke-RestMethod "$WebUrl/api/invoice/receive" -TimeoutSec 5
        Write-Check ($inv.status -eq "ok") "GET invoice mock receiver" ($inv | ConvertTo-Json -Compress)
    } catch {
        Write-Check $false "GET invoice mock receiver" $_.Exception.Message
    }

    try {
        $body = '{"requestId":"golive-verify-0001","test":true,"documentType":"boleta","ticketNumber":1,"folio":1,"currency":"CLP","paymentMethod":"Efectivo","cashier":"verify","seller":{"rut":"76.000.000-0","businessName":"Verify"},"lines":[{"code":"T","name":"Test","quantity":1,"unitPrice":100,"discountPercent":0,"total":100}],"totals":{"net":84,"tax":16,"taxPercent":19,"discount":0,"total":100}}'
        $emit = Invoke-RestMethod -Method Post -Uri "$WebUrl/api/invoice/receive" -ContentType "application/json; charset=utf-8" -Body $body -TimeoutSec 8
        Write-Check ([bool]$emit.success) "POST invoice mock emit" $emit.providerDocumentId
    } catch {
        Write-Check $false "POST invoice mock emit" $_.Exception.Message
    }
}

# 5) API central (si está instalada como caja principal)
$apiDll = Join-Path $PublishRoot "API\GrunflexPOS.API.dll"
if (Test-Path $apiDll) {
    try {
        $apiHealth = Invoke-RestMethod "http://127.0.0.1:7279/health/live" -TimeoutSec 5
        Write-Check ($true) "GET API :7279/health/live" ($apiHealth | ConvertTo-Json -Compress)
    } catch {
        Write-Check $false "GET API :7279/health/live" $_.Exception.Message
    }
}

# 6) Bridge health with DPAPI token
if (-not $SkipBridge) {
    $token = $null
    try { $token = Get-BridgeToken } catch { $token = $null }
    if (-not $token) {
        Write-Check $false "Token DPAPI bridge" "No existe token.dpapi - inicie el bridge"
    } else {
        Write-Check $true "Token DPAPI bridge" "leido OK"
        try {
            $headers = @{ Authorization = "Bearer $token" }
            $b = Invoke-RestMethod "$BridgeUrl/health" -Headers $headers -TimeoutSec 5
            Write-Check ($b.status -eq "ok") "GET $BridgeUrl/health" ($b | ConvertTo-Json -Compress)
            $ports = Invoke-RestMethod "$BridgeUrl/api/serial-ports" -Headers $headers -TimeoutSec 5
            Write-Check ($null -ne $ports) "GET /api/serial-ports" ("count=" + @($ports).Count)
        } catch {
            Write-Check $false "Hardware Bridge API" $_.Exception.Message
        }
    }
}

# 7) Unit tests (optional quick gate)
if (-not $SkipTests) {
    Write-Host ""
    Write-Host "Ejecutando tests unitarios..." -ForegroundColor Cyan
    dotnet test (Join-Path $root "GrunflexPOS.Web.Tests\GrunflexPOS.Web.Tests.csproj") --nologo -v q --filter "FullyQualifiedName!~WebLicensingCloudE2eTests"
    Write-Check ($LASTEXITCODE -eq 0) "dotnet test GrunflexPOS.Web.Tests" "exit=$LASTEXITCODE"
}

Write-Host ""
Write-Host "Resultado: $passed pass, $failed fail" -ForegroundColor $(if ($failed -eq 0) { "Green" } else { "Yellow" })
if ($failed -gt 0) { exit 1 }
exit 0
