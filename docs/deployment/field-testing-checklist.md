## PosEdge — Checklist Field Testing (LAN)

### Instalación (PC 1 = Server)
- Ejecutar `PosEdge-Setup.exe` como administrador
- Esperar “Validando sistema…” y “Sistema listo”
- Abrir “Self-check”
  - Resultado esperado: **PASS** (o **DEGRADED** sin fallas críticas)
- Abrir “Backup ahora” (solo para validar que genera ZIP)

### Instalación (PC 2 = Terminal)
- Ejecutar `PosEdge-Setup.exe` como administrador
- Esperar “Conectando terminal…” y “Sistema listo”
- Abrir “Self-check”
- Validar que `cached-server.txt` existe en `ProgramData\PosEdge\config`

### Pruebas de resiliencia (mínimas)
- Reiniciar PC Server (sin desinstalar) y validar que el terminal reconecta solo
- Cambiar IP del Server (DHCP renew / cambiar cable/NIC) y validar reconexión terminal
- Parar `PosEdgeApi` y validar que `PosEdgeGuardian` lo reinicia
- Parar `PosEdgeWorkers` y validar reinicio
- Borrar reglas de firewall y validar que Guardian las re-crea (API/Discovery/mDNS)

### Recuperación y soporte
- Ejecutar “Exportar diagnóstico” y confirmar que genera un `.zip`
- Confirmar que hay backups en `ProgramData\PosEdge\backups\`

