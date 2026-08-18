$ErrorActionPreference = "Stop"

function Ensure-CleanDir([string] $path) {
  if (Test-Path $path) { Remove-Item -Recurse -Force $path }
  New-Item -ItemType Directory -Path $path | Out-Null
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Push-Location $repoRoot

try {
  $deploy = Join-Path $repoRoot "deploy"
  Ensure-CleanDir $deploy
  Ensure-CleanDir (Join-Path $deploy "server")
  Ensure-CleanDir (Join-Path $deploy "terminal")

  dotnet --info | Out-Host

  dotnet clean | Out-Host
  dotnet build -c Release | Out-Host

  dotnet publish "src/PosEdge.Api/PosEdge.Api.csproj" -c Release -o "deploy/server" | Out-Host
  dotnet publish "src/PosEdge.Terminal/PosEdge.Terminal.csproj" -c Release -o "deploy/terminal" | Out-Host

  Write-Host ""
  Write-Host "[OK] Publish listo:"
  Write-Host " - deploy\server"
  Write-Host " - deploy\terminal"
}
finally {
  Pop-Location
}

