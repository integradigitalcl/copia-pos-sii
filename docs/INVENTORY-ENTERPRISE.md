# Inventario transaccional enterprise (GrunflexPOS.API)

## Principios

- **PostgreSQL / SQLite commerce (`grunflex.db`)** es la autoridad en commit (no cache UI).
- **Serializable** + **lock por fila ordenado por `ProductId`** (anti-deadlock).
- **Validar stock después del lock**, nunca antes.
- **Idempotencia** vía `RequestId` + tablas multicaja / `MulticajaIdempotencyRunner`.
- **Reintentos** solo en operaciones idempotentes (`InventoryRetryPolicy`).
- **Auditoría** en `InventoryMovements` (no solo `Productos.Stock`).

## Tablas (commerce)

| Tabla | Rol |
|-------|-----|
| `InventoryStocks` | Stock autoritativo + `AverageUnitCost` + `RowVersion` |
| `InventoryMovements` | Historial completo por operación |
| `InventoryReservations` | Esqueleto futuro (`ReservationsEnabled = false`) |
| `Productos.Stock` | Espejo para sync/catálogo (actualizado en cada movimiento) |

Bootstrap: `CommerceInventorySchemaInitializer` (arranque API) + backfill desde `Productos`.

## Flujo commit venta

```
1. BeginTransaction(Serializable)
2. Idempotency lookup (RequestId)
3. ValidateCajaSessionFreshAsync (DB viva)
4. Resolver productos (sin validar stock)
5. InventoryTransactionService.ApplyStockChangesAsync
   a. LockProductsOrderedAsync (FOR UPDATE en Npgsql)
   b. Validar disponible = OnHand - Reserved
   c. Mutar InventoryStocks + Productos.Stock
   d. Insert InventoryMovements
6. Insert Venta + detalle + movimiento caja
7. Commit
8. SyncChangeRecorder inventory (SignalR)
```

## Servicios (`GrunflexPOS.API/Inventory/`)

| Servicio | Responsabilidad |
|----------|-----------------|
| `InventoryLockService` | `FOR UPDATE` (Postgres) / lectura ordenada en Serializable (SQLite) |
| `InventoryTransactionService` | lock → validar → mutar → auditoría |
| `InventoryMovementService` | Registro de movimientos |
| `InventoryRetryPolicy` | Backoff + jitter; serialization/deadlock/busy |
| `DatabaseErrorTranslator` | `PosConcurrencyException` + mensajes operador |
| `CommerceActiveSessionValidator` | Sesión/caja/usuario fresh |
| `InventoryReservationService` | Preparación reservas (deshabilitado) |

## DTO multicaja (auditoría terminal)

Opcionales en venta / devolución / anulación:

- `TerminalId`, `TerminalCode`, `UserSessionId`, `BranchId`

El cliente WPF los rellena automáticamente vía `MulticajaTerminalAuditHelper` (`App.Terminal` / `identity.json`) en venta, devolución y anulación (incluye cola offline y reenvío HTTP).

## Errores operador

| Código | Mensaje |
|--------|---------|
| `SERIALIZATION_FAILURE` | Otra terminal completó el cambio primero… |
| `DEADLOCK` | Conflicto temporal entre cajas… |
| `STOCK_INSUFICIENTE` | Tras lock, disponible real insuficiente |

## Costo promedio ponderado

En entradas (`QuantityDelta > 0`):

`newAvg = (oldQty * oldAvg + inbound * unitCost) / (oldQty + inbound)`

Salidas registran `UnitCost` = promedio vigente.

## Cache

- **No** usar cache para validación de commit.
- **Sí** cachear catálogo; invalidación realtime ya existente (`MulticajaSyncCoordinator`).

## Próximos pasos

- Compras / transferencias / ajustes API dedicados reutilizando `InventoryTransactionService`.
- Habilitar `InventoryReservationService.ReservationsEnabled` + TTL carrito.
- Migrar commerce a Npgsql central si aplica despliegue solo-PostgreSQL.
