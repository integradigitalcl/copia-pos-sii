param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [string]$PublicKeyPemPath = ""
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$webProject = Join-Path $root "GrunflexPOS.Web/GrunflexPOS.Web.csproj"
$apiProject = Join-Path $root "GrunflexPOS.API/GrunflexPOS.API.csproj"
$macScripts = Join-Path $PSScriptRoot "macos"
$staging = Join-Path $root "artifacts/macos-installer"
$payload = Join-Path $staging "_payload"
$webOut = Join-Path $payload "web"
$apiOut = Join-Path $payload "API"
$appName = "Grunflex POS Web.app"
$appRoot = Join-Path $staging $appName
$outDir = Join-Path $PSScriptRoot "out"
$zipPath = Join-Path $outDir "GrunflexPOS-Web-macOS-Catalina.zip"

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $webOut, $apiOut, $outDir | Out-Null

Write-Host "Publicando web ($Configuration, osx-x64, self-contained)..."
dotnet publish $webProject -c $Configuration -r osx-x64 --self-contained true -o $webOut
if ($LASTEXITCODE -ne 0) { throw "Falló publish de GrunflexPOS.Web para macOS." }

Write-Host "Publicando API ($Configuration, osx-x64, self-contained)..."
dotnet publish $apiProject -c $Configuration -r osx-x64 --self-contained true -o $apiOut
if ($LASTEXITCODE -ne 0) { throw "Falló publish de GrunflexPOS.API para macOS." }

foreach ($path in @(
    (Join-Path $apiOut "appsettings.local.json"),
    (Join-Path $apiOut "appsettings.Development.json")
)) {
    if (Test-Path $path) { Remove-Item $path -Force }
}

function Set-AllowDemoOff([string]$settingsPath) {
    if (-not (Test-Path $settingsPath)) { return }
    $raw = Get-Content $settingsPath -Raw
    if ($raw -match '"AllowDemoCredentials"\s*:\s*true') {
        $raw = $raw -replace '"AllowDemoCredentials"\s*:\s*true', '"AllowDemoCredentials": false'
        Set-Content -Path $settingsPath -Value $raw -Encoding UTF8
    }
}
Set-AllowDemoOff (Join-Path $webOut "appsettings.json")

if (-not $PublicKeyPemPath) {
    $PublicKeyPemPath = Join-Path $env:LOCALAPPDATA "GrunflexPOS\data\licensing-public.pem"
}
if (Test-Path $PublicKeyPemPath) {
    $pem = (Get-Content $PublicKeyPemPath -Raw).Trim()
    if ($pem) {
        $jsonPem = ConvertTo-Json $pem -Compress
        foreach ($tgt in @(
            (Join-Path $webOut "appsettings.json"),
            (Join-Path $apiOut "appsettings.json")
        )) {
            if (-not (Test-Path $tgt)) { continue }
            $content = Get-Content $tgt -Raw
            if ($content -match '"Licensing"\s*:\s*\{') {
                if ($content -match '"PublicKeyPem"\s*:\s*"[^"]*"') {
                    $content = [regex]::Replace($content, '"PublicKeyPem"\s*:\s*"[^"]*"', '"PublicKeyPem": ' + $jsonPem)
                } else {
                    $content = [regex]::Replace($content, '("Licensing"\s*:\s*\{)', '$1' + "`n    " + '"PublicKeyPem": ' + $jsonPem + ',')
                }
            }
            Set-Content -Path $tgt -Value $content -Encoding UTF8
        }
    }
}

Copy-Item (Join-Path $macScripts "*.sh") $payload -Force
Copy-Item (Join-Path $macScripts "README-macOS.md") $payload -Force

Write-Host "Armando $appName..."
$contents = Join-Path $appRoot "Contents"
$macosDir = Join-Path $contents "MacOS"
$resources = Join-Path $contents "Resources"
New-Item -ItemType Directory -Force -Path $macosDir, $resources | Out-Null
Copy-Item (Join-Path $macScripts "app-bundle\Info.plist") (Join-Path $contents "Info.plist") -Force

# Launcher con LF (sin BOM) para que macOS ejecute el shebang.
$launcherSrc = Join-Path $macScripts "app-bundle\GrunflexPOSWeb"
$launcherDst = Join-Path $macosDir "GrunflexPOSWeb"
$launcherText = [System.IO.File]::ReadAllText($launcherSrc) -replace "`r`n", "`n" -replace "`r", "`n"
[System.IO.File]::WriteAllText($launcherDst, $launcherText, (New-Object System.Text.UTF8Encoding $false))

Copy-Item -Path (Join-Path $payload "*") -Destination $resources -Recurse -Force

# Quitar carpeta temporal de payload suelta; el ZIP/DMG usan el .app
Remove-Item $payload -Recurse -Force

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path $appRoot -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "Paquete macOS listo: $zipPath"
Write-Host "App bundle: $appRoot"
