using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Idempotency;
using GrunflexPOS.API.Inventory;

namespace GrunflexPOS.API.Services;

public sealed partial class MulticajaVentaProcessor
{
    private readonly MulticajaIdempotencyRunner _idempotencyRunner;

    public MulticajaVentaProcessor(
        PosCommerceDbContext db,
        ILogger<MulticajaVentaProcessor> log,
        MulticajaIdempotencyRunner idempotencyRunner,
        CommerceActiveSessionValidator sessionValidator,
        InventoryTransactionService inventory,
        InventoryRetryPolicy inventoryRetry)
    {
        _db = db;
        _log = log;
        _idempotencyRunner = idempotencyRunner;
        _sessionValidator = sessionValidator;
        _inventory = inventory;
        _inventoryRetry = inventoryRetry;
    }

    public Task<MulticajaVentaCommitResponse> CommitAsync(MulticajaVentaCommitRequest req, CancellationToken ct) =>
        _idempotencyRunner.ExecuteAsync(
            IdempotencyOperationType.VentaCommit,
            req,
            r => r.RequestId,
            r => r.CajaId,
            token => CommitCoreAsync(req, token),
            r => (r.Ok, r.Ok ? "venta" : null, r.Ok ? r.VentaId.ToString() : null),
            ct);
}
