using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Idempotency;
using GrunflexPOS.API.Inventory;

namespace GrunflexPOS.API.Services;

public sealed partial class MulticajaInventarioProcessor
{
    private readonly MulticajaIdempotencyRunner _idempotencyRunner;

    public MulticajaInventarioProcessor(
        PosCommerceDbContext db,
        ILogger<MulticajaInventarioProcessor> log,
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

    public Task<MulticajaInventarioAjusteResponse> AjustarAsync(MulticajaInventarioAjusteRequest req, CancellationToken ct) =>
        _idempotencyRunner.ExecuteAsync(
            IdempotencyOperationType.InventarioAjuste,
            req,
            r => r.RequestId,
            r => r.CajaId,
            token => AjustarCoreAsync(req, token),
            r => (r.Ok, r.Ok ? "inventario_ajuste" : null, r.Ok ? $"{r.ProductoId}:{r.StockNuevo}" : null),
            ct);
}
