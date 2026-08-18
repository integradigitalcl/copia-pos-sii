@echo off
setlocal enableextensions

REM Best-effort stop of PosEdge.Api process.
REM If you install as a Windows Service later, replace this with service stop.

echo [OK] Stopping PosEdge.Api...
taskkill /IM PosEdge.Api.exe /T /F >NUL 2>&1

echo [OK] Done.
exit /b 0

