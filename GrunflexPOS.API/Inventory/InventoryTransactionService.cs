using GrunflexPOS.API.Data;
using GrunflexPOS.API.Services;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Inventory;

/// <summary>
/// Dominio transaccional de inventario: lock → validar → mutar → auditoría.
/// El caller debe abrir <see cref="System.Data.IsolationLevel.Serializable"/> antes de invocar.
/// </summary>
public sealed class InventoryTransactionService
{
    private readonly PosCommerceDbContext _db;
    private readonly InventoryLockService _locks;
    private readonly InventoryMovementService _movements;
    private readonly InventoryReservationService _reservations;
    private readonly SyncChangeRecorder? _syncChanges;
    private readonly ILogger<InventoryTransactionService> _log;

    public InventoryTransactionService(
        PosCommerceDbContext db,
        InventoryLockService locks,
        InventoryMovementService movements,
        InventoryReservationService reservations,
        ILogger<InventoryTransactionService> log,
        SyncChangeRecorder? syncChanges = null)
    {
        _db = db;
        _locks = locks;
        _movements = movements;
        _reservations = reservations;
        _syncChanges = syncChanges;
        _log = log;
    }

    public async Task<InventoryApplyResult> ApplyStockChangesAsync(
        InventoryStockChangeRequest request,
        CancellationToken ct)
    {
        var aggregated = request.Lines
            .Where(l => l.ProductId > 0 && l.QuantityDelta != 0)
            .GroupBy(l => l.ProductId)
            .Select(g => new InventoryLineChange
            {
                ProductId = g.Key,
                QuantityDelta = g.Sum(x => x.QuantityDelta),
                UnitCostForInbound = g.Where(x => x.UnitCostForInbound.HasValue)
                    .Select(x => x.UnitCostForInbound)
                    .LastOrDefault()
            })
            .OrderBy(l => l.ProductId)
            .ToList();

        if (aggregated.Count == 0)
            return InventoryApplyResult.Success();

        var productIds = aggregated.Select(l => l.ProductId).ToList();
        var locked = await _locks.LockProductsOrderedAsync(productIds, ct);
        var lockMap = locked.ToDictionary(r => r.ProductId);

        var reservedByProduct = new Dictionary<int, int>();
        if (InventoryReservationService.ReservationsEnabled)
        {
            foreach (var id in productIds)
                reservedByProduct[id] = await _reservations.GetReservedQuantityAsync(id, ct);
        }

        var entities = await _db.InventoryStocks
            .Where(s => productIds.Contains(s.ProductId))
            .OrderBy(s => s.ProductId)
            .ToListAsync(ct);

        var productos = await _db.Productos
            .Where(p => productIds.Contains(p.Id))
            .OrderBy(p => p.Id)
            .ToDictionaryAsync(p => p.Id, ct);

        foreach (var line in aggregated)
        {
            if (!lockMap.TryGetValue(line.ProductId, out var snap))
                return InventoryApplyResult.Fail("PRODUCTO_NO_ENCONTRADO",
                    "Producto sin registro de inventario.", line.ProductId);

            var entity = entities.First(e => e.ProductId == line.ProductId);
            if (entity.RowVersion != snap.RowVersion)
            {
                _log.LogWarning(
                    "inventory.concurrency token mismatch productId={P} expected={E} actual={A}",
                    line.ProductId, snap.RowVersion, entity.RowVersion);
                return InventoryApplyResult.Fail("CONCURRENCY_CONFLICT",
                    DatabaseErrorTranslator.OperatorMessage(DatabaseErrorTranslator.SerializationFailure),
                    line.ProductId);
            }

            var reserved = reservedByProduct.GetValueOrDefault(line.ProductId);
            var available = entity.QuantityOnHand - reserved;
            if (line.QuantityDelta < 0 && !request.AllowNegativeStock)
            {
                var needed = -line.QuantityDelta;
                if (available < needed)
                {
                    productos.TryGetValue(line.ProductId, out var prod);
                    var name = prod?.Nombre ?? $"#{line.ProductId}";
                    _log.LogWarning(
                        "inventory.validation failed productId={P} available={Av} reserved={R} needed={N}",
                        line.ProductId, available, reserved, needed);
                    return InventoryApplyResult.Fail("STOCK_INSUFICIENTE",
                        $"Stock insuficiente para «{name}» (disponible {available}, solicitado {needed}).",
                        line.ProductId);
                }
            }

            var prevQty = entity.QuantityOnHand;
            var newQty = prevQty + line.QuantityDelta;
            if (!request.AllowNegativeStock && newQty < 0)
                newQty = 0;

            decimal newAvg = entity.AverageUnitCost;
            if (line.QuantityDelta > 0)
            {
                var unitCost = line.UnitCostForInbound ?? productos.GetValueOrDefault(line.ProductId)?.Costo ?? 0m;
                newAvg = ApplyWeightedAverage(prevQty, entity.AverageUnitCost, line.QuantityDelta, unitCost);
            }

            entity.QuantityOnHand = newQty;
            entity.AverageUnitCost = newAvg;
            entity.RowVersion++;
            entity.UpdatedAtUtc = DateTime.UtcNow;

            if (productos.TryGetValue(line.ProductId, out var producto))
                producto.Stock = newQty;

            var unitForMovement = line.QuantityDelta > 0
                ? (line.UnitCostForInbound ?? newAvg)
                : entity.AverageUnitCost;

            _movements.RecordMovement(
                line.ProductId,
                request.MovementType,
                line.QuantityDelta,
                newQty,
                unitForMovement,
                newAvg,
                request.ReferenceType,
                request.ReferenceId,
                request.Operation);
        }

        await _db.SaveChangesAsync(ct);

        if (_syncChanges != null)
        {
            await _syncChanges.RecordInventoryAsync(
                productIds,
                request.Operation.CajaId != Guid.Empty ? request.Operation.CajaId : null,
                ct).ConfigureAwait(false);
        }

        return InventoryApplyResult.Success();
    }

    public static decimal ApplyWeightedAverage(int oldQty, decimal oldAvg, int inboundQty, decimal unitCost)
    {
        if (inboundQty <= 0)
            return oldAvg;
        var newQty = oldQty + inboundQty;
        if (newQty <= 0)
            return unitCost;
        return ((oldQty * oldAvg) + (inboundQty * unitCost)) / newQty;
    }
}
