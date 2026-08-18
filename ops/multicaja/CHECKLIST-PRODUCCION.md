# Checklist multicaja — producción

Use este checklist antes de dar por operativa una instalación **principal + adicional**.

## Caja principal (servidor)

- [ ] PC encendida y en la misma red LAN que las adicionales
- [ ] Instalador **Multicaja** v0.3.5+ (o `PosEdge-Setup.exe`) como **Caja principal**
- [ ] Servicio **GrunflexPOSAPI** en ejecución (puerto **7279**)
- [ ] Firewall: puerto **7279/TCP** permitido — **automático** en instalador y `desplegar-api-repo` (admin). Manual: `ops\multicaja\reparar-firewall-multicaja.ps1` o `verificar-conexion-multicaja.ps1 -RepararFirewall`
- [ ] Health OK:
  ```powershell
  Invoke-RestMethod "http://127.0.0.1:7279/health/live"
  Invoke-RestMethod "http://127.0.0.1:7279/health/ready"
  ```
- [ ] IP LAN anotada (`ipconfig` → IPv4, ej. `192.168.1.7`)
- [ ] Empresa y al menos un usuario admin creados
- [ ] Inventario cargado en **principal** (ajustes pasan por API → stock central)

## Caja adicional (terminal)

- [ ] Instalador **Multicaja** como **Caja adicional** con IP LAN de la principal (no `127.0.0.1`)
- [ ] `grunflex-terminal.json` en Escritorio con **CajaId** GUID válido (desde principal → Conectar más cajas)
- [ ] POS arranca sin error de CajaId / localhost
- [ ] Login con usuario de la principal (ej. admin)
- [ ] Pie de pantalla: **Conectado** (verde)

## Verificación automática

En la **principal**:

```powershell
powershell -ExecutionPolicy Bypass -File "C:\Program Files\PosEdge\SetupExtras\verificar-conexion-multicaja.ps1" -ServerIp 192.168.1.7
```

Criterios de éxito:

- [ ] `health/live` y `health/ready` → 200
- [ ] `api/terminals` → **≥ 1** terminal (ideal: 2 con adicional encendida)
- [ ] En adicional: `Api.BaseUrl` = `http://<IP-principal>:7279/` (no localhost)

## Inventario multicaja

- [ ] Ajustes de stock solo vía pantalla Inventario (API central, no SQLite local en adicional)
- [ ] Tras venta en una caja, la otra refleja stock en **≤ 3 s** (SignalR + sync incremental)
- [ ] Simulación local (desarrollo):
  ```powershell
  dotnet run --project tools\Multicaja.SimTest\Multicaja.SimTest.csproj -c Release
  ```

## Si algo falla

| Síntoma | Acción |
|---------|--------|
| CajaId inválido | Regenerar plantilla en principal; copiar `grunflex-terminal.json` |
| Api localhost en adicional | `SetupExtras\corregir-caja-adicional-api.ps1 -ServerIp <IP>` |
| 0 terminales | Reabrir POS en adicional; revisar firewall y API |
| Stock desincronizado | Reiniciar API; no editar stock manual en SQLite; usar Inventario |
