[CmdletBinding()]
param(
    [string]$InstallPath = (Join-Path $env:LOCALAPPDATA "GrunflexPOS\HardwareBridge"),
    [switch]$RegisterTask,
    [switch]$Start
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($env:OS -ne "Windows_NT") {
    throw "The hardware bridge is Windows-only."
}

$project = Resolve-Path (Join-Path $PSScriptRoot "..\GrunflexPOS.HardwareBridge.csproj")
$publishPath = Join-Path ([IO.Path]::GetTempPath()) ("grunflex-bridge-" + [guid]::NewGuid().ToString("N"))

try {
    Write-Host "Publishing hardware bridge..."
    dotnet publish $project `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $publishPath `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true

    New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
    Copy-Item (Join-Path $publishPath "*") $InstallPath -Recurse -Force
}
finally {
    if (Test-Path $publishPath) {
        Remove-Item $publishPath -Recurse -Force
    }
}

$executable = Join-Path $InstallPath "GrunflexPOS.HardwareBridge.exe"
if (-not (Test-Path $executable)) {
    throw "Publish did not produce $executable"
}

if ($RegisterTask) {
    $taskName = "GrunflexPOS Hardware Bridge"
    $action = New-ScheduledTaskAction -Execute $executable
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
        -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
        -Principal $principal -Force | Out-Null
    Write-Host "Registered scheduled task: $taskName"
}

if ($Start) {
    Start-Process -FilePath $executable -WorkingDirectory $InstallPath -WindowStyle Hidden
    Write-Host "Started $executable"
}

Write-Host "Installed at $InstallPath"
Write-Host "The DPAPI token will be created on first launch."
