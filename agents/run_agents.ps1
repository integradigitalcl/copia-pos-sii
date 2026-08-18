$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot

if (!(Test-Path ".\.venv\Scripts\python.exe")) {
  python -m venv .venv
}

.\.venv\Scripts\python.exe -m pip install --upgrade pip
.\.venv\Scripts\python.exe -m pip install -r ".\agents\requirements.txt"

if (-not $env:OPENAI_API_KEY) {
  throw "OPENAI_API_KEY is not set in environment."
}

.\.venv\Scripts\python.exe ".\agents\orchestrator.py"

Write-Host "Done. Check:"
Write-Host " - agents/state/plan.md"
Write-Host " - agents/state/runner_report.md"
