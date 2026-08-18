using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Idempotency;
using GrunflexPOS.API.Inventory;

namespace GrunflexPOS.API.Services;

public sealed partial class MulticajaAnulacionProcessor
{
    private readonly MulticajaIdempotencyRunner _idempotencyRunner;

    public MulticajaAnulacionProcessor(
        PosCommerceDbContext db,
        ILogger<MulticajaAnulacionProcessor> log,
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

    public Task<MulticajaAnularVentaResponse> AnularAsync(MulticajaAnularVentaRequest req, CancellationToken ct) =>
        _idempotencyRunner.ExecuteAsync(
            IdempotencyOperationType.VentaAnular,
            req,
            r => r.RequestId,
            r => r.CajaId,
            token => AnularCoreAsync(req, token),
            r => (r.Ok, r.Ok ? "venta_anulacion" : null, r.Ok ? r.NumeroTicket.ToString() : null),
            ct);
}
