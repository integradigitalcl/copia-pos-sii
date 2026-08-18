# Amplia NumberOfBoxes en grunflex_api.db para registrar caja adicional.
param(
    [int] $Cajas = 5,
    [string] $ActivationId = "GF-20260524-6036",
    [string] $ApiDb = ""
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$tool = Join-Path $repo "ops\multicaja\AmpliarLicenciaCajas\AmpliarLicenciaCajas.csproj"

if ([string]::IsNullOrWhiteSpace($ApiDb)) {
    $ApiDb = Join-Path $env:ProgramData "GrunflexPOS\data\grunflex_api.db"
}
if (-not (Test-Path $ApiDb)) {
    Write-Error "No se encontró $ApiDb"
}

Write-Host "Ampliando licencia: $ApiDb → $Cajas cajas (ActivationId=$ActivationId)"
dotnet build $tool -c Release | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dll = Join-Path $repo "ops\multicaja\AmpliarLicenciaCajas\bin\Release\net8.0\AmpliarLicenciaCajas.dll"
dotnet $dll $ApiDb $Cajas $ActivationId
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`nProbando registro de 2ª terminal..."
$base = "http://127.0.0.1:7279"
$body = @{
    MachineFingerprint = ("adicional-test-" + [Guid]::NewGuid().ToString("N").Substring(0, 24))
    MachineName = "CAJA-ADICIONAL-TEST"
    Version = "field"
    ActivationId = $ActivationId
} | ConvertTo-Json
$r = Invoke-RestMethod "$base/api/terminals/register" -Method Post -Body $body -ContentType "application/json"
Write-Host "register granted=$($r.granted) slots=$($r.slotsInUse)/$($r.slotsTotal) reason=$($r.reason)"
$terms = Invoke-RestMethod "$base/api/terminals"
Write-Host "Terminales activas: $($terms.Count)"
