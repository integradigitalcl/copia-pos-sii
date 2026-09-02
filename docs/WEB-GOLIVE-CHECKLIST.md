# Checklist go-live — Grunflex POS Web

Use esta lista antes de instalar en un comercio real. Marque cada ítem en el equipo de producción.

## Instalación

- [ ] Ejecutar `ops/web/compile-web-installer.ps1` (o `-PublishOnly` si no hay Inno Setup).
- [ ] **Caja principal:** instalar con tipo *Caja principal* (incluye API `:7279` como servicio Windows; no requiere instalar .NET por separado).
- [ ] **Caja adicional:** instalar con tipo *Caja adicional* (detecta la principal en la red automáticamente; si falla, configurar en Config → Cajas).
- [ ] Instalar con `GrunflexPOS-Web-Setup.exe`.
- [ ] Verificar acceso directo abre Chrome en `http://127.0.0.1:7373`.
- [ ] Leer credenciales iniciales en `%LocalAppData%\GrunflexPOS\initial-admin-credentials.txt` y eliminar el archivo.
- [ ] Cambiar contraseña admin en el primer ingreso (modal obligatorio).

## Seguridad

- [ ] `Security:AllowDemoCredentials` es **false** en producción (`appsettings.json` del publish).
- [ ] No usar `admin/admin` ni `cajero/cajero` en producción.
- [ ] Crear usuarios cajero desde Config → Usuarios con contraseñas propias.
- [ ] Servidor web solo en loopback (`127.0.0.1:7373`), no exponer a la LAN.

## Licenciamiento

- [ ] Activar licencia GFv2 (Config → Licencia o pantalla pre-login si `RequireLicense: true`).
- [ ] Verificar módulos: Multicaja, Soporte, CloudBackup según plan.
- [ ] Probar sync: Config → Licencia → Sincronizar con servidor.
- [ ] Con CloudBackup: probar “Subir respaldo ahora”.

## Multicaja (si aplica)

- [ ] API central en `:7279` accesible desde la caja.
- [ ] Config → Cajas: URL API, rol terminal, multicaja habilitada.
- [ ] Auto-registro de caja OK (estado “Conectada”).
- [ ] Desconectar API y verificar cola offline; reconectar y replay.

## Operación diaria

- [ ] Login con usuario real (no demo).
- [ ] Venta efectivo, tarjeta, mixto y crédito.
- [ ] Anular venta (permiso `cancel`).
- [ ] Ajuste inventario y sync multicaja.
- [ ] Apertura/cierre caja con conteo manual.
- [ ] Impresión boleta y apertura cajón (Hardware Bridge en `:7390`).
- [ ] Lector serial COM (Config → Lector → Probar / conectar) o pistola USB teclado en Ventas.
- [ ] Báscula: etiquetas precio/peso y/o **Leer báscula** en Ventas.

## Facturación electrónica

- [ ] Config → Facturación: RUT/razón social emisor, IVA, tipo boleta/factura.
- [ ] Endpoint del proveedor (o mock local `http://127.0.0.1:7373/api/invoice/receive`).
- [ ] **Probar conectividad** y **Emitir documento de prueba** OK.
- [ ] Cobrar una venta con “Emitir documento electrónico” marcado; ver mensaje de éxito.
- [ ] Revisar historial en Config → Facturación.
- [ ] Con factura: completar RUT y razón social del receptor en el cobro.

## Correo

- [ ] Config → Correo: SMTP, **Preset Gmail** si aplica, **Probar correo**.
- [ ] Tras una venta, confirmar recepción del resumen (con PDF si aplica).

## Respaldo

- [ ] Respaldar `%LocalAppData%\GrunflexPOS\grunflex-pos.db` con el servidor detenido.
- [ ] Probar export JSON desde Config → Base de datos.
- [ ] Con licencia CloudBackup: confirmar respaldo en API (`GET /api/backups`).

## Verificación automática rápida (dev/staging)

```powershell
# Publish web + bridge
powershell -ExecutionPolicy Bypass -File ops/web/compile-web-installer.ps1 -PublishOnly

# Arranque conjunto (web :7373 + bridge :7390)
powershell -ExecutionPolicy Bypass -File ops/web/run-web-production.ps1 -PublishDirectory artifacts/web-installer/web -BridgeDirectory artifacts/web-installer/bridge -ShowCredentialsHint

# Checklist automático
powershell -ExecutionPolicy Bypass -File ops/web/verify-golive.ps1 -PublishRoot artifacts/web-installer
```

En producción el web lee el token del bridge desde  
`%LOCALAPPDATA%\GrunflexPOS\HardwareBridge\token.dpapi` (mismo DPAPI que el bridge).

## Script de preparación licencias (dev/staging)

```powershell
dotnet run --project ops/web/SetupLicensingE2e/SetupLicensingE2e.csproj
dotnet run --project GrunflexPOS.API --no-launch-profile --environment Testing --urls http://127.0.0.1:7279
powershell -ExecutionPolicy Bypass -File ops/web/run-web-production.ps1
```
