@echo off
setlocal enableextensions

REM Resetea cache local de terminal (SQLite + IDs persistidos).
REM Usar SOLO para reiniciar desde cero una caja en pruebas.

echo [OK] Closing terminal (best-effort)...
taskkill /IM PosEdge.Terminal.exe /T /F >NUL 2>&1

set "ROOT=%~dp0"

set "DATA_DIR=%LocalAppData%\PosEdge"

echo [WARN] Deleting "%DATA_DIR%" ...
if exist "%DATA_DIR%" (
  rmdir /S /Q "%DATA_DIR%"
)

echo [OK] Reset done.
exit /b 0

