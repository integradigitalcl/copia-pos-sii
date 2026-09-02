param(
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$project = Join-Path $root "GrunflexPOS.Web/GrunflexPOS.Web.csproj"
$output = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $root "artifacts/web" } else { $OutputDirectory }

dotnet publish $project --configuration Release --output $output --no-self-contained
Write-Host "Publicación lista: $output"
