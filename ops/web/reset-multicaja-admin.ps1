param(
    [string]$DatabasePath = "",
    [string]$UserName = "admin",
    [string]$Password = "",
    [string]$DisplayName = "Administrador"
)

$ErrorActionPreference = "Stop"
$db = if ($DatabasePath) { $DatabasePath } else {
    Join-Path $env:ProgramData "GrunflexPOS\data\grunflex.db"
}

if (-not (Test-Path $db)) {
    throw "No se encontró la base multicaja: $db"
}

if (-not $Password) {
    $credFiles = @(
        (Join-Path $env:ProgramData "GrunflexPOS\config\initial-admin-credentials.txt"),
        (Join-Path $env:LOCALAPPDATA "GrunflexPOS\initial-admin-credentials.txt")
    )
    foreach ($file in $credFiles) {
        if (-not (Test-Path $file)) { continue }
        foreach ($line in Get-Content $file) {
            if ($line -match '^\s*Contrase(n|ñ)a:\s*(.+)$') {
                $Password = $Matches[2].Trim()
                break
            }
        }
        if ($Password) { break }
    }
}

if (-not $Password) {
    $Password = -join ((48..57 + 65..90 + 97..122) | Get-Random -Count 16 | ForEach-Object { [char]$_ })
}

$seedCandidates = @(
    (Join-Path $PSScriptRoot "Tools\SeedAdmin"),
    (Join-Path $PSScriptRoot "Tools\SeedAdmin\SeedAdmin.exe"),
    (Join-Path (Split-Path $PSScriptRoot -Parent) "ops\multicaja\SeedAdmin"),
    (Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..\..\") -ErrorAction SilentlyContinue) "ops\multicaja\SeedAdmin")
) | Where-Object { $_ -and (Test-Path $_) }

$seedExe = $seedCandidates | Where-Object { $_ -like "*.exe" } | Select-Object -First 1
$seedProject = $seedCandidates | Where-Object { Test-Path (Join-Path $_ "SeedAdmin.csproj") } | Select-Object -First 1
$webDb = Join-Path $env:LOCALAPPDATA "GrunflexPOS\grunflex-pos.db"
$seedArgs = @($db, $UserName, $Password, $DisplayName)
if (Test-Path $webDb) { $seedArgs += @("--web-db", $webDb) }

if ($seedExe) {
    & $seedExe @seedArgs
} elseif ($seedProject) {
    dotnet run --project $seedProject -- @seedArgs
} else {
    throw "No se encontró SeedAdmin. Reinstale el POS Web o ejecute desde el repositorio de desarrollo."
}
if ($LASTEXITCODE -ne 0) { throw "No se pudo crear/verificar el usuario $UserName." }

$content = @"
Grunflex POS — credenciales iniciales
Generado: $(Get-Date -Format 'yyyy-MM-dd HH:mm')
Equipo: $env:COMPUTERNAME

Usuario: $UserName
Contraseña: $Password

Use estas credenciales para ingresar al POS Web con multicaja activa.
"@

$shared = Join-Path $env:ProgramData "GrunflexPOS\config\initial-admin-credentials.txt"
New-Item -ItemType Directory -Force -Path (Split-Path $shared) | Out-Null
Set-Content -Path $shared -Value $content -Encoding UTF8
$local = Join-Path $env:LOCALAPPDATA "GrunflexPOS\initial-admin-credentials.txt"
New-Item -ItemType Directory -Force -Path (Split-Path $local) | Out-Null
Set-Content -Path $local -Value $content -Encoding UTF8

Write-Host $content -ForegroundColor Yellow
