# Multicaja enterprise (GrunflexPOS2 + GrunflexPOS.API)

Arquitectura tolerante a fallos de terreno: identidad persistente, heartbeat separado, delta sync, SignalR de invalidación, reconnect coordinado y cola offline con estados.

## Identidad de terminal

| Campo | Origen |
|--------|--------|
| `InstallationId` | GUID generado en `%LocalAppData%\GrunflexPOS\terminal\identity.json` |
| `TerminalId` | Asignado por API en registro |
| `TerminalToken` | Secreto local; en servidor solo `TerminalTokenHash` (SHA-256) |
| `CajaId` / `BranchId` | Config + registro |

**No** se usa `COMPUTERNAME`, hostname ni IP como identidad principal.

### API

- `POST /api/terminals/enterprise/register`
- `POST /api/terminals/enterprise/heartbeat` (throttle ≥45s en tabla `TerminalHeartbeats`; `Reconnect=true` fuerza persistencia)
- `POST /api/terminals/enterprise/validate`
- `GET /api/terminals/enterprise/heartbeats` (dashboard)
- `GET /api/terminals/enterprise/audit/{terminalId}`

### Cliente

- `TerminalIdentityStore`, `TerminalService`, `TerminalContextService` (cache TTL 2 min)

## Delta sync

- `GET /api/multicaja/sync/changes?cursor=&limit=&domains=`
- Tabla `MulticajaSyncChangeLogs` (cursor = `Id` autoincremental)
- `SyncChangeRecorder` registra cambios tras ventas (stock)
- Cliente: `MulticajaIncrementalSyncService` → pull parcial de productos/usuarios según dominios afectados
- Estado local: `multicaja-sync-state.json` (`LastCursor`, `RetryCount`)

## SignalR

- Hub: `/hubs/multicaja-sync`
- Eventos mínimos: `product-updated`, `inventory-adjusted`, `cashier-updated`, `caja-updated`, `sync-reset`
- Cliente: `MulticajaHubClient` → invalidación cache + delta sync

## Reconnect coordinado

`MulticajaReconnectOrchestrator` (serializado):

1. SignalR reconnect  
2. Heartbeat (`Reconnect=true`) + registro  
3. Replay cola offline (`OfflineReplayService`)  
4. Delta sync o full pull según capabilities  
5. Invalidación UI (`InventoryCacheInvalidator`, etc.)

Fases: `Online | Offline | Reconnecting | Syncing | Degraded`

## Capabilities

`GET /api/multicaja/capabilities` — el cliente adapta:

```json
{
  "incrementalSync": true,
  "signalR": true,
  "idempotency": true,
  "offlineReplay": true,
  "heartbeatVersion": 2,
  "terminalIdentity": true
}
```

## Cola offline

Estados: `Pending | Sending | Completed | Failed | Poisoned`  
Backoff exponencial; replay exclusivo; idempotencia vía cabeceras (ver `docs/IDEMPOTENCY-ENTERPRISE.md`).

## Health

- `GET /health/live` — proceso vivo (sin BD)
- `GET /health/ready` — `ApiDbContext` + `PosCommerceDbContext` conectables

## Logs estructurados

Prefijos en `pos.log` / API: `multicaja.terminal.*`, `multicaja.sync.*`, `multicaja.reconnect`, `multicaja.hub`, `multicaja.heartbeat`, `multicaja.replay`, `multicaja.cache`, `multicaja.debounce`, `multicaja.ui refresh`.

## Cache invalidation y UI reactiva

Ver `App.MulticajaRealtime` (Caches, EventBus, Coordinator, DeltaSync).

Flujo: SignalR → invalidación → debounce IDs → `GET sync/products/by-ids` → shadow SQLite → `LocalEventBus` → `ReactiveViewRefresh` en Inventario/Ventas/Productos.

## Bootstrap

`TerminalBootstrapOrchestrator` exige: config válida (no localhost en adicional), API online, registro terminal, sync inicial, SQLite sombra migrada.
