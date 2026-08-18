using GrunflexPOS.API.Commerce;
using GrunflexPOS.API.Data;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Inventory;

public sealed class InventoryLockService
{
    private readonly PosCommerceDbContext _db;
    private readonly ILogger<InventoryLockService> _log;

    public InventoryLockService(PosCommerceDbContext db, ILogger<InventoryLockService> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Bloquea filas de inventario en orden ascendente por <see cref="InventoryStock.ProductId"/> (anti-deadlock).
    /// Debe invocarse dentro de transacción <see cref="System.Data.IsolationLevel.Serializable"/>.
    /// </summary>
    public async Task<IReadOnlyList<LockedInventoryRow>> LockProductsOrderedAsync(
        IReadOnlyList<int> productIds,
        CancellationToken ct)
    {
        var ordered = productIds.Where(id => id > 0).Distinct().OrderBy(id => id).ToList();
        if (ordered.Count == 0)
            return Array.Empty<LockedInventoryRow>();

        await EnsureStockRowsExistAsync(ordered, ct);

        var isPostgres = _db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
        IReadOnlyList<LockedInventoryRow> rows;

        if (isPostgres)
            rows = await LockPostgresForUpdateAsync(ordered, ct);
        else
            rows = await LockSqliteSerializableAsync(ordered, ct);

        _log.LogInformation(
            "inventory.lock acquired count={N} productIds={Ids}",
            rows.Count,
            string.Join(",", ordered));

        return rows;
    }

    private async Task EnsureStockRowsExistAsync(List<int> productIds, CancellationToken ct)
    {
        var existing = await _db.InventoryStocks
            .Where(s => productIds.Contains(s.ProductId))
            .Select(s => s.ProductId)
            .ToListAsync(ct);

        var missing = productIds.Except(existing).ToList();
        if (missing.Count == 0)
            return;

        var productos = await _db.Productos
            .Where(p => missing.Contains(p.Id))
            .OrderBy(p => p.Id)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var p in productos)
        {
            _db.InventoryStocks.Add(new InventoryStock
            {
                ProductId = p.Id,
                QuantityOnHand = p.Stock,
                ReservedQuantity = 0,
                AverageUnitCost = p.Costo > 0 ? p.Costo : 0m,
                RowVersion = 1,
                UpdatedAtUtc = now
            });
        }

        if (productos.Count > 0)
            await _db.SaveChangesAsync(ct);
    }

    private async Task<IReadOnlyList<LockedInventoryRow>> LockPostgresForUpdateAsync(
        List<int> orderedIds,
        CancellationToken ct)
    {
        var rows = new List<LockedInventoryRow>();
        foreach (var id in orderedIds)
        {
            var row = await _db.Database
                .SqlQuery<PostgresLockRow>($"""
                    SELECT s."ProductId" AS ProductId,
                           s."QuantityOnHand" AS QuantityOnHand,
                           s."ReservedQuantity" AS ReservedQuantity,
                           s."AverageUnitCost" AS AverageUnitCost,
                           s."RowVersion" AS RowVersion
                    FROM "InventoryStocks" AS s
                    WHERE s."ProductId" = {id}
                    FOR UPDATE
                    """)
                .FirstOrDefaultAsync(ct);

            if (row == null)
                throw new InvalidOperationException($"InventoryStock missing for product {id} after ensure.");

            rows.Add(new LockedInventoryRow
            {
                ProductId = row.ProductId,
                QuantityOnHand = row.QuantityOnHand,
                ReservedQuantity = row.ReservedQuantity,
                AverageUnitCost = row.AverageUnitCost,
                RowVersion = row.RowVersion
            });
        }

        return rows;
    }

    private async Task<IReadOnlyList<LockedInventoryRow>> LockSqliteSerializableAsync(
        List<int> orderedIds,
        CancellationToken ct)
    {
        var entities = await _db.InventoryStocks
            .Where(s => orderedIds.Contains(s.ProductId))
            .OrderBy(s => s.ProductId)
            .ToListAsync(ct);

        if (entities.Count != orderedIds.Count)
        {
            var found = entities.Select(e => e.ProductId).ToHashSet();
            var missing = orderedIds.First(id => !found.Contains(id));
            throw new InvalidOperationException($"InventoryStock missing for product {missing}.");
        }

        await _db.Productos
            .Where(p => orderedIds.Contains(p.Id))
            .OrderBy(p => p.Id)
            .LoadAsync(ct);

        return entities.Select(e => new LockedInventoryRow
        {
            ProductId = e.ProductId,
            QuantityOnHand = e.QuantityOnHand,
            ReservedQuantity = e.ReservedQuantity,
            AverageUnitCost = e.AverageUnitCost,
            RowVersion = e.RowVersion
        }).ToList();
    }

    private sealed class PostgresLockRow
    {
        public int ProductId { get; set; }
        public int QuantityOnHand { get; set; }
        public int ReservedQuantity { get; set; }
        public decimal AverageUnitCost { get; set; }
        public long RowVersion { get; set; }
    }
}
