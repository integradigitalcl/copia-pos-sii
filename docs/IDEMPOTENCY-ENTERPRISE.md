# Idempotencia enterprise — Grunflex POS multicaja

## Capas

1. **Cliente (WPF)** — `RequestId` estable, cabeceras HTTP, hash canónico, cola offline con el mismo id.
2. **API (`IdempotencyRecords`)** — PostgreSQL o SQLite en `ApiDbContext`; advisory lock en PostgreSQL.
3. **Comercio (`grunflex.db`)** — tablas `Multicaja*Idempotency` legacy (doble cinturón).

## Flujo venta (pseudocódigo)

```
Cliente:
  requestId = Guid (persistido en cola si offline)
  hash = SHA256(canonical_json(body))
  POST /api/multicaja/ventas/commit
    Headers: X-Grunflex-Request-Id, X-Grunflex-Terminal, X-Grunflex-Caja-Id, X-Grunflex-Payload-Hash
    Body: { requestId, cajaId, items, ... }

API MulticajaIdempotencyRunner:
  begin = IdempotencyService.BeginAsync(VentaCommit, descriptor)
  if begin.ReplayCompleted → return deserialize(stored JSON)  // mismo ticket/ventaId
  if begin.RejectedHashMismatch → 409 IDEMPOTENCY_REJECTED
  if begin.RejectedConcurrent → 409 (otro worker procesando)
  response = CommitCoreAsync()  // transacción Serializable en grunflex.db
  IdempotencyService.CompleteAsync(recordId, 200|409, json, resourceType, resourceId)

PostgreSQL (dentro de Begin):
  BEGIN
  SELECT pg_advisory_xact_lock(k1, k2)  -- derivado de requestId|operation
  SELECT * FROM IdempotencyRecords WHERE RequestId AND OperationType FOR UPDATE
  ...
  COMMIT
```

## Replay offline

- Archivo cola: `{requestId}.json`
- `PayloadSha256` = hash del JSON en disco
- Al replay: validar hash → `CommitVentaAsync` con mismas cabeceras → servidor devuelve resultado almacenado si ya completó

## Limpieza

`IdempotencyCleanupHostedService` — cada 6 h purga registros `Completed`/`Failed` con `ExpiresAt` vencido (30 días).

## Diagnóstico

`GET /api/idempotency/status?requestId=...&operationType=VentaCommit` (requiere JWT operador).
