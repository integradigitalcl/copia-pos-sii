# Simula caja principal + caja adicional contra la API multicaja (puerto 7279 en loopback).
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$proj = Join-Path $repoRoot "tools\Multicaja.SimTest\Multicaja.SimTest.csproj"

Write-Host "Compilando simulador multicaja..."
dotnet build $proj -c Release | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
dotnet run --project $proj -c Release --no-build -- $repoRoot
exit $LASTEXITCODE
