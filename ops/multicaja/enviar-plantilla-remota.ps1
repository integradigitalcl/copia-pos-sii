param(
    [string] $ComputerName = "DESKTOP-3I4K47K",
    [string] $ServerIp = "192.168.1.7",
    [int] $HttpPort = 8765
)

$ErrorActionPreference = "Stop"
$localScript = Join-Path $PSScriptRoot "copiar-plantilla-a-adicional.ps1"

Invoke-Command -ComputerName $ComputerName -FilePath $localScript -ArgumentList $ServerIp, $HttpPort
