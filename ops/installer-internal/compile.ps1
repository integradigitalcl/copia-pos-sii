# Compila el instalador interno de Grunflex (LicenseIssuer + LicenseManager).
# Uso: .\compile.ps1
$ErrorActionPreference = "Stop"

$here = $PSScriptRoot
Push-Location $here
try {
    Write-Host "1/2  Construyendo staging interno..." -ForegroundColor Cyan
    & (Join-Path $here "build-staging.ps1")

    $iscc = $null
    $candidatos = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidatos) {
        if (Test-Path $c) { $iscc = $c; break }
    }
    if (-not $iscc) {
        throw "No se encontró ISCC.exe (Inno Setup 6). Instálalo desde https://jrsoftware.org/isdl.php"
    }

    Write-Host "2/2  Compilando instalador interno con Inno Setup..." -ForegroundColor Cyan
    & $iscc (Join-Path $here "Grunflex.LicenseIssuer.iss")

    $out = Join-Path $here "out"
    Write-Host ""
    Write-Host "Instalador interno generado en: $out" -ForegroundColor Green
}
finally {
    Pop-Location
}
