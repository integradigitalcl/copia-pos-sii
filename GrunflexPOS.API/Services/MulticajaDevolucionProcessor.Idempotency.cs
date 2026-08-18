using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Idempotency;
using GrunflexPOS.API.Inventory;

namespace GrunflexPOS.API.Services;

public sealed partial class MulticajaDevolucionProcessor
{
    private readonly MulticajaIdempotencyRunner _idempotencyRunner;

    public MulticajaDevolucionProcessor(
        PosCommerceDbContext db,
        ILogger<MulticajaDevolucionProcessor> log,
        MulticajaIdempotencyRunner idempotencyRunner,
        InventoryTransactionService inventory,
        InventoryRetryPolicy inventoryRetry)
    {
        _db = db;
        _log = log;
        _idempotencyRunner = idempotencyRunner;
        _inventory = inventory;
        _inventoryRetry = inventoryRetry;
    }

    public Task<MulticajaDevolucionLineaResponse> DevolverLineaAsync(MulticajaDevolucionLineaRequest req,
        CancellationToken ct) =>
        _idempotencyRunner.ExecuteAsync(
            IdempotencyOperationType.VentaDevolucionLinea,
            req,
            r => r.RequestId,
            r => r.CajaId,
            token => DevolverLineaCoreAsync(req, token),
            r => (r.Ok, r.Ok ? "venta_devolucion" : null, r.Ok ? r.NumeroTicket.ToString() : null),
            ct);
}
