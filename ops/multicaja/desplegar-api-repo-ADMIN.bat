@echo off
:: Ejecutar como administrador: copia API a PosEdge + servicio Windows GrunflexPOSAPI
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"%~dp0desplegar-api-repo.ps1\"'"
pause
