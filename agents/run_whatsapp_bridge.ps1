$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot

if (!(Test-Path ".\.venv\Scripts\python.exe")) {
  python -m venv .venv
}

.\.venv\Scripts\python.exe -m pip install --upgrade pip
.\.venv\Scripts\python.exe -m pip install -r ".\agents\requirements.txt"

if (-not $env:WA_VERIFY_TOKEN) {
  throw "WA_VERIFY_TOKEN is not set."
}

Write-Host "Starting WhatsApp bridge at http://0.0.0.0:8080"
.\.venv\Scripts\python.exe -m uvicorn agents.whatsapp_bridge:app --host 0.0.0.0 --port 8080
