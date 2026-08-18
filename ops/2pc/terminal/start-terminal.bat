@echo off
setlocal enableextensions

REM ===== PosEdge terminal (client) =====
REM Expects:
REM - appsettings.Production.json beside the exe
REM - terminalId persisted automatically under %%LocalAppData%%\PosEdge\terminal-config\
REM
REM Usage:
REM   1) Edit deploy\terminal\appsettings.Production.json -> Api:BaseUrl points to SERVER IP (not localhost)
REM   2) Run this bat

set "ROOT=%~dp0"
set "TERM_DIR=%ROOT%..\..\..\deploy\terminal"

if not exist "%TERM_DIR%\PosEdge.Terminal.exe" (
  echo [ERR] No encuentro "%TERM_DIR%\PosEdge.Terminal.exe"
  echo Ejecuta primero: ops\2pc\build-release.ps1
  exit /b 2
)

echo [OK] Starting PosEdge.Terminal...
pushd "%TERM_DIR%"
start "PosEdge.Terminal" "%TERM_DIR%\PosEdge.Terminal.exe"
popd

exit /b 0

