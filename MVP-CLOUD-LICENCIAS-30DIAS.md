# MVP 30 dias - Licencias + Nube + Respaldo + Soporte

Este plan define como convertir `GrunflexPOS2` en un producto con suscripcion, manteniendo operacion offline.

## Objetivo del MVP

- Licenciamiento robusto (firma asimetrica, sin clave privada en cliente).
- Respaldo automatico en nube por suscripcion.
- Panel minimo de soporte y estado de servicio.
- Flujo offline-first: el POS sigue vendiendo sin internet.

## Arquitectura propuesta

- **POS local (`GrunflexPOS2`)**
  - Genera backup local.
  - Cifra y sube backup cuando hay internet.
  - Consulta estado de suscripcion/licencia (con cache local).
  - Muestra estado de respaldo y soporte.
- **API cloud (`GrunflexPOS.API`)**
  - Endpoints de licencia (activate/refresh/status).
  - Endpoints de respaldo (upload/list/download/delete).
  - Endpoints de soporte (crear ticket, listar, responder).
  - Persistencia de metadatos en DB.
- **Storage de respaldos (bucket)**
  - Blob storage por negocio/caja.
  - Archivos cifrados en cliente antes de subir.
- **Issuer privado (solo tu)**
  - Herramienta separada para emitir/renovar/revocar licencias.
  - Usa clave privada (nunca en repositorio del POS).

## Modelo de planes (MVP)

- `Base`: uso local.
- `Pro`: respaldo en nube + soporte preferente.
- `MultiCaja+`: Pro + modulos online.

## Semana 1 (dias 1-7): base tecnica y seguridad

1. Crear contrato de licencia v2:
   - `licenseId`, `customerId`, `storeName`, `features[]`, `expUtc`, `hwFingerprint`, `issuedAt`, `sig`.
2. Migrar validacion de `HMAC` a `RSA/ECDSA` con public key en `GrunflexPOS2`.
3. Definir `ILicenseStateProvider` como fuente unica de entitlements.
4. Integrar provider en `CajasView`, `CajaView`, `ConectarMasCajasWindow`.
5. Preparar `LicenseIssuer` privado (proyecto aparte, no distribuido).

## Semana 2 (dias 8-14): API minima de licencias y suscripcion

1. Crear en `GrunflexPOS.API` endpoints:
   - `POST /api/licensing/activate`
   - `POST /api/licensing/refresh`
   - `GET /api/licensing/status`
2. Persistir estado servidor:
   - cliente, licencia, expiracion, modulos, estado.
3. Cache local en POS:
   - ultima validacion buena (`lastValidUtc`), tolerancia offline.
4. UI de licencia en POS:
   - ver estado, vigencia, plan, activar/actualizar.

## Semana 3 (dias 15-21): respaldos nube

1. Definir formato de backup:
   - zip + manifiesto JSON (version, fecha, tamano, hash).
2. Cifrado cliente:
   - AES-GCM por archivo, llave derivada de secreto de tenant.
3. Endpoints en `GrunflexPOS.API`:
   - `POST /api/backups/upload`
   - `GET /api/backups`
   - `GET /api/backups/{id}/download`
   - `DELETE /api/backups/{id}`
4. Job local POS:
   - horario configurable, reintento exponencial, cola local.
5. Pantalla POS:
   - ultimo backup, proximo, estado, restaurar.

## Semana 4 (dias 22-30): soporte + endurecimiento + salida

1. Endpoints soporte:
   - `POST /api/support/tickets`
   - `GET /api/support/tickets`
   - `POST /api/support/tickets/{id}/reply`
2. Integracion UX:
   - boton "Solicitar soporte" en POS con contexto tecnico.
3. Alertas:
   - licencia por vencer, backup fallido, sin sincronizacion.
4. Hardening:
   - rotacion de llaves publicas, auditoria de eventos, limites API.
5. Release piloto:
   - 2-5 clientes, feedback y ajuste de precios.

## Cambios concretos por proyecto

### En `GrunflexPOS2`

- Nuevo namespace:
  - `Licensing/Contracts/*`
  - `Licensing/ILicenseStateProvider.cs`
  - `Licensing/LicenseStateProvider.cs`
  - `Backup/BackupOrchestrator.cs`
  - `Backup/BackupEncryptionService.cs`
  - `Support/SupportClient.cs`
- Nueva vista:
  - `Views/LicenciaView.xaml` (estado, activar, renovar, diagnostico).
- Ajustes:
  - `App.xaml.cs` para bootstrap de licencia + scheduler backup.
  - Reemplazar lecturas directas de flags por `ILicenseStateProvider`.

### En `GrunflexPOS.API`

- Nuevo modulo:
  - `Controllers/LicensingController.cs`
  - `Controllers/BackupsController.cs`
  - `Controllers/SupportController.cs`
  - `Services/LicensingService.cs`
  - `Services/BackupStorageService.cs`
  - `Services/SupportService.cs`
  - `Models/*` y `DTOs/*` para licensing/backups/support.
- Seguridad:
  - auth por API key/tenant + rate limiting + logs de auditoria.

### Fuera del repo del cliente (privado)

- `Grunflex.LicenseIssuer` (CLI o app interna).
- Almacen seguro de clave privada (idealmente Key Vault/HSM).

## Costos de infraestructura (estimado inicial)

Supuesto: 50 clientes, 1 GB respaldo/cliente/mes promedio.

- API app pequena: USD 20-60/mes.
- DB administrada pequena: USD 15-50/mes.
- Blob storage (50-100 GB): USD 2-5/mes.
- Trafico/egress y logs: USD 10-40/mes.
- Monitoreo y backups del backend: USD 10-30/mes.

Total aproximado inicial: **USD 60-185/mes**.

## Politicas recomendadas (MVP)

- Retencion backups:
  - diario 30 dias, semanal 12 semanas, mensual 6 meses.
- Offline grace:
  - licencia cacheada valida por 7-15 dias sin internet.
- Soporte:
  - Base: correo, Pro: prioridad, MultiCaja+: prioridad alta.

## Riesgos y mitigaciones

- **Riesgo:** clonacion de licencias.
  - **Mitigacion:** firma asimetrica + huella de equipo + revocacion.
- **Riesgo:** internet inestable.
  - **Mitigacion:** cola local, reintentos, modo offline.
- **Riesgo:** restauraciones incompletas.
  - **Mitigacion:** hash/verificacion y restore test periodico.

## Entregables del dia 1 (inicio inmediato)

1. Diseñar contrato `LicensePayload v2`.
2. Crear `ILicenseStateProvider` y migrar 3 vistas clave.
3. Crear `LicenciaView` inicial (solo lectura de estado).
4. Definir DTOs/Controller skeleton en `GrunflexPOS.API` para licensing.

---

Si quieres, en la siguiente iteracion ejecuto directamente los **entregables del dia 1** en codigo y dejamos el primer commit del modulo listo.
