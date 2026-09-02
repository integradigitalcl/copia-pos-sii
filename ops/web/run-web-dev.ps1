param(
    [int]$Port = 7373,
    [switch]$NoBrowser
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$project = Join-Path $root "GrunflexPOS.Web/GrunflexPOS.Web.csproj"
$url = "http://127.0.0.1:$Port"

Write-Host "Iniciando Grunflex POS Web en $url"
$process = Start-Process dotnet -ArgumentList @("watch", "--project", $project, "run", "--urls", $url) -WorkingDirectory $root -PassThru
Start-Sleep -Seconds 2
if (-not $NoBrowser) {
    Start-Process $url
}

Write-Host "Proceso iniciado (PID $($process.Id)). Cierra esa ventana o detén el proceso para apagar el POS."
Wait-Process -Id $process.Id
