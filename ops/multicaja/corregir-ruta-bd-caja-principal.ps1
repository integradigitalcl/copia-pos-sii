# Corrige appsettings que apuntan a ProgramData\GrunflexPOS\grunflex.db (ruta incorrecta).
# La BD real está en ProgramData\GrunflexPOS\data\grunflex.db
$ErrorActionPreference = "Stop"

$correct = "Data Source=$env:ProgramData\GrunflexPOS\data\grunflex.db;Cache=Shared"
$paths = @(
    "$env:ProgramData\GrunflexPOS\config\appsettings.local.json",
    "$env:LOCALAPPDATA\GrunflexPOS\config\appsettings.local.json"
)

foreach ($p in $paths) {
    if (-not (Test-Path $p)) { continue }
    $j = Get-Content $p -Raw | ConvertFrom-Json
    $old = $j.ConnectionStrings.Default
    $j.ConnectionStrings.Default = $correct
    if (-not $j.PSObject.Properties['Multicaja']) {
        $j | Add-Member -NotePropertyName Multicaja -NotePropertyValue ([pscustomobject]@{ UseApiOnlyClient = $false })
    }
    $j.TerminalRole = "server"
    $j | ConvertTo-Json -Depth 6 | Set-Content $p -Encoding UTF8
    Write-Host "Actualizado: $p"
    Write-Host "  Antes: $old"
    Write-Host "  Ahora: $correct"
}

# Si quedó una BD vacía en la ruta incorrecta, no la usamos.
$wrongDb = "$env:ProgramData\GrunflexPOS\grunflex.db"
if (Test-Path $wrongDb) {
    Rename-Item $wrongDb "$wrongDb.mal-ruta.bak" -Force -ErrorAction SilentlyContinue
    Write-Host "Renombrada BD en ruta incorrecta: $wrongDb.mal-ruta.bak"
}

Write-Host ""
Write-Host "Listo. Cierre el POS, ejecute Reparar sistema (opcional) y vuelva a abrir Grunflex POS."
