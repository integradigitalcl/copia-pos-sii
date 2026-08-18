param(
    [string] $ServerIp = "192.168.1.7",
    [int] $HttpPort = 8765,
    [string] $ComputerName = "DESKTOP-3I4K47K"
)

$ErrorActionPreference = "Stop"
$desktop = [Environment]::GetFolderPath("Desktop")
if ([string]::IsNullOrWhiteSpace($desktop)) {
    $desktop = Join-Path $env:USERPROFILE "Desktop"
}
$dest = Join-Path $desktop "grunflex-terminal.json"
$url = "http://${ServerIp}:${HttpPort}/grunflex-terminal.json"

Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
Write-Output "Plantilla descargada: $dest"
