@echo off
setlocal enableextensions

REM ===== PosEdge server (API) =====
REM Expects:
REM - appsettings.Production.json beside the exe
REM - ASPNETCORE_ENVIRONMENT=Production
REM
REM Ports:
REM - API/SignalR: http://0.0.0.0:5071
REM - Metrics:     /metrics
REM - Health:      /health/live, /health/ready

set "ROOT=%~dp0"
set "API_DIR=%ROOT%..\..\..\deploy\server"

if not exist "%API_DIR%\PosEdge.Api.exe" (
  echo [ERR] No encuentro "%API_DIR%\PosEdge.Api.exe"
  echo Ejecuta primero: ops\2pc\build-release.ps1
  exit /b 2
)

set ASPNETCORE_ENVIRONMENT=Production

echo [OK] Starting PosEdge.Api (Production)...
pushd "%API_DIR%"
start "PosEdge.Api" /min "%API_DIR%\PosEdge.Api.exe"
popd

echo [OK] Started. Check:
echo  - http://SERVER_IP:5071/health/live
echo  - http://SERVER_IP:5071/health/ready
echo  - http://SERVER_IP:5071/metrics
exit /b 0

