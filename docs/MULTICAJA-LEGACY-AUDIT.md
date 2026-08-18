# Auditoría multicaja: API-only vs SQLite/UNC (2026)

Este documento lista **qué sigue atado a SQLite local o UNC**, qué ya pasó por **API central**, y el **riesgo** por módulo. Objetivo: eliminar doble verdad en cajas secundarias con `UseApiOnlyClient`.

## Leyenda de riesgo

| Nivel | Significado |
|-------|-------------|
| **Bajo** | Solo UI local / sombra; no afecta stock ni caja central. |
| **Medio** | Lectura local que puede desalinearse con el servidor si no se refresca. |
| **Alto** | Escritura o lógica de negocio aún en SQLite sombra o UNC compartido. |

## Ya centralizado en API (caja secundaria API-only)

| Módulo | API / comportamiento |
|--------|---------------------|
| **Ventas** | `POST /api/multicaja/ventas/commit` — stock, ticket, sesión, idempotencia. |
| **Anulación ticket** | `POST /api/multicaja/ventas/anular` — revierte stock, movimiento caja, permisos. |
| **Devolución línea** | `POST /api/multicaja/ventas/devolucion-linea` — stock + totales + movimiento. |
| **Cierre de caja** | `POST /api/multicaja/caja-sesiones/cerrar` — unicidad sesión, snapshot esperado/dif. |
| **Ingreso / retiro efectivo** | `POST /api/multicaja/caja-sesiones/movimiento` — transacción serializable, `MovimientosCaja`, totales sesión, idempotencia `MulticajaMovimientoCajaIdempotency`, logs `multicaja.ingreso` / `multicaja.retiro`. |
| **Login / sesión / auto-caja** | Endpoints existentes bajo `/api/multicaja/*`. |
| **Catálogo productos (lookup)** | `ProductoApiService` + cabeceras LAN; sin fallback SQLite en API-only (`ResilientProductoLookupService`). |
| **Cola offline** | `multicaja-*` kinds (incluye `multicaja-movimiento-caja`) + replay con límites y `FailedPermanent`. |

## Aún con DbContext / SQLite en secundaria (revisar)

| Área | Uso típico | Riesgo | Notas |
|------|------------|--------|-------|
| **`GrunflexDbContext` general** | Sombras `terminal_shadow.db`: empresa mínima, caja espejo, migraciones. | Bajo–Medio | No es BD de verdad; no debe usarse para stock/ventas reales en API-only. |
| **`InicializadorService`** | Placeholder empresa en sombra. | Bajo | |
| **`LoginWindow` (no API-only)** | Abrir sesión vía `CajaService` + EF. | Alto si UNC | Flujo legacy principal. |
| **`CajaService` / ingresos / retiros`** | Solo flujo **principal** o secundaria **no** API-only. | **Bajo** en API-only | En cliente API-only, `VentasView` usa solo API + cola opcional; no escribe `MovimientosCaja` en sombra. |
| **`VentaRepository` / JSON historial** | Historial visual de tickets en cliente. | Medio | Es caché; debe converger con servidor tras cada operación OK. |
| **`CorteView.GuardarCorteHistorico`** | JSON **local** `%LocalAppData%\GrunflexPOS\cortes_historico_local.json` o `Multicaja:CortesHistoricoLocalPath`. | **Bajo** | Ya no hay UNC hardcodeado; no es fuente de verdad de negocio. |
| **`ConectarMasCajasWindow` / UNC** | Plantillas con SMB. | Medio | Instalador nuevo ya orienta a API-only. |
| **Otras vistas (Productos, Inventario, reportes)** | CRUD o listados vía EF local. | Medio–Alto | Prioridad siguiente: lecturas vía API o pantallas deshabilitadas en API-only. |
| **Licencias / updates / telemetría** | HTTP propio; no POS commerce. | Bajo | |

## Híbrido a eliminar (propuesta)

1. **Cualquier** `CajaService` que escriba `CajaSesiones` / `MovimientosCaja` en secundaria API-only sin pasar por API (ingresos/retiros ya cubiertos desde `VentasView`).
2. ~~**Historial cortes** en UNC fijo (`CorteView`).~~ **Hecho:** solo archivo local configurable.
3. **Fallback** de productos a SQLite en cliente API-only (ya desactivado en código).

## Referencias UNC restantes (no son “verdad” de caja)

- **`App.xaml.cs`**, **`AppConfig`**, **`SqliteConnectionStringHelpers`**, **`SmbShareConnectionBootstrap`**: detección y saneo de cadenas UNC para modo **legacy** o transición.
- **`CajasView`**, **`ConectarMasCajasWindow`**, **scripts `ops/installer`**: mensajes o plantillas que mencionan `\\servidor\GrunflexPOS`.
- **Riesgo**: solo si el operador mantiene `UseApiOnlyClient: false` y UNC como `ConnectionStrings:Default`.

## Seguridad LAN (implementado en esta fase)

- `Multicaja:RequireSharedSecret` en API: si `true`, exige `Multicaja:SharedSecret` no vacío y cabecera `X-Grunflex-Multicaja-Key`.
- `ProductosController` usa el mismo `MulticajaSharedSecretFilter` que multicaja.
- Cliente envía `X-Grunflex-Caja-Id` y `X-Grunflex-Terminal` en operaciones multicaja y en `ProductoApiService` (HttpClient compartido).

## Pruebas sugeridas (API-only)

1. Principal: usuario con `CANCELAR_TICKETS` o `Admin`; secundaria: anular ticket del turno actual → stock vuelve en servidor.
2. Devolución parcial → stock y total en servidor; historial local se ajusta.
3. Cierre con monto contado → sesión cerrada en servidor; `App.MulticajaSesionEnServidor` nulo.
4. Con `RequireSharedSecret: true` en ambos lados, sin clave → rechazo de API en operaciones protegidas.
5. Cola: desconectar LAN, venta/anulación/**ingreso o retiro**/cierre con `EnqueueWhenOffline` o `EnqueueVentaWhenOffline`, reconectar → replay y purga de JSON.
6. **Venta simultánea** (dos clientes): tickets distintos; sin doble decremento de stock gracias a transacción serializable en servidor.
7. **Doble anulación** mismo ticket: segunda llamada idempotente / error controlado según estado.
8. **Stock concurrente**: dos ventas mismo SKU hasta agotar stock; una debe fallar con conflicto claro.
9. **Timeout / caída API**: verificar cola o bloqueo según `MulticajaBlockCriticalWhenOffline`.
10. **Arranque**: revisar `pos.log` por `multicaja.config`, `multicaja.validation`, `multicaja.startup` en terminal API-only.

## Hardening producción (cliente API-only)

| Mecanismo | Qué hace |
|-----------|----------|
| **`MulticajaLocalWriteGuard`** | En `SaveChanges*`, bloquea escrituras EF locales de `VentaEntity`, `DetalleVenta`, `MovimientoCaja`, `CajaSesion` si `UseApiOnlyClient`; log `multicaja.guard blocked_local_ef`. |
| **`MulticajaRiskScanner`** | Tras validación de arranque, al iniciar cola, y en cada cambio de conectividad: UNC en cadena activa, API en loopback, cola al tope, reloj UTC sospechoso, estado offline/degraded; log `multicaja.risk` / `multicaja.guard` CRITICAL si híbrido UNC. |
| **`MulticajaDiagnostics.WriteStartupDump`** | Un volcado en log (`multicaja.diagnostics`) con API URL, CajaId, flags de bloqueo, métricas de cola, conectividad. |
| **Cola offline** | SHA-256 del payload (`PayloadSha256`), migración automática de ítems viejos, `replay_journal.log`, escritura atómica `.tmp`→rename, **un solo replay concurrente** (`SemaphoreSlim`), logs `multicaja.integrity` / `multicaja.health`. |
| **Límites cola** | `Multicaja:OfflineQueueMaxPendingItems` (default 250, 0=sin límite), `Multicaja:OfflineQueueMaxTotalBytes` (default 5242880); encolado rechazado con `multicaja.risk enqueue_rejected`. |
| **Arranque** | Prueba escritura en carpeta cola, `GET cajas/{id}/exists` (401/404/503 → bloqueo o aviso), reloj UTC fuera de rango. |
| **API** | Logs unificados `multicaja.idempotent *` en hits de idempotencia (venta, anulación, devolución, cierre, movimiento caja). |

## Checklist pruebas producción (resumen operador + técnico)

| # | Escenario | Pasos | Esperado | Logs / señales |
|---|-----------|--------|------------|-----------------|
| 1 | 3 cajas simultáneas | 3 clientes API-only, misma API, CajaId distintos | Ventas con tickets distintos; stock coherente | Sin `blocked_local_ef` en secundarias |
| 2 | Venta concurrente mismo SKU | Dos ventas a la vez última unidad | Una OK, otra conflicto API | Serilog/API sin doble stock negativo |
| 3 | Desconexión / reconexión | Cable LAN off 30s, on | Estado `Offline`→`Online`; replay si hay cola | `multicaja.replay`, `multicaja.connect` |
| 4 | Caída API | Detener servicio API en principal | Secundaria: ventas bloqueadas o cola según config | `multicaja.risk`, `enqueue_rejected` si cola llena |
| 5 | Reinicio secundaria con cola | Encolar con red off, reiniciar POS, encender red | Replay automático | `replay_journal.log`, ítems purgados al OK |
| 6 | Cierre / devolución concurrente | Dos operaciones mismo ticket | Idempotencia o error claro | `multicaja.idempotent` en API |
| 7 | SharedSecret inválido | Clave cliente ≠ servidor | 401 en exists; bloqueo monetario si aplica | `multicaja.validation`, `multicaja.risk` |
| 8 | Corrupción cola | Editar manualmente un `.json` de cola | `checksum_fail`, `FailedPermanent` | `multicaja.integrity checksum_fail` |
| 9 | Timeout largo | Firewall bloqueando 7279 intermitente | Reintentos con backoff; no duplicado servidor | `multicaja.retry` |
| 10 | Post-prueba | — | Inventario y sesiones alineados con principal | Comparar `grunflex.db` servidor vs tickets |

## Clasificación riesgo residual (post-hardening)

| Nivel | Componente | Notas |
|-------|------------|--------|
| **CRÍTICO** | `HYBRID_UNC_IN_CONNECTIONSTRING` en API-only | No debería ocurrir si `Cargar()` fuerza sombra; si aparece en `multicaja.risk`, investigar config manual. |
| **MEDIO** | Pantallas EF (Productos, Inventario, `CajasView`) en secundaria | Pueden intentar mutar entidades no bloqueadas aún; revisar UX “solo lectura” en fases futuras. |
| **BAJO** | Histórico tickets JSON local, `cortes_historico_local.json` | Caché / informe local; no es verdad de stock. |
| **Listo producción** | Ventas/anulaciones/devoluciones/cierre/movimiento caja vía API, cola con checksum, guard EF crítico, startup + risk scan | Monitorear `pos.log` y journal de cola. |
