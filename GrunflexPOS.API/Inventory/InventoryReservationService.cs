using GrunflexPOS.API.Data;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Inventory;

/// <summary>Reservas de inventario (deshabilitado; esqueleto para carrito / hold futuro).</summary>
public sealed class InventoryReservationService
{
    /// <summary>Feature flag futuro (carrito / hold). Mantener en false en producción actual.</summary>
    public static bool ReservationsEnabled { get; set; }

    private readonly PosCommerceDbContext _db;
    private readonly ILogger<InventoryReservationService> _log;

    public InventoryReservationService(PosCommerceDbContext db, ILogger<InventoryReservationService> log)
    {
        _db = db;
        _log = log;
    }

    public Task<InventoryApplyResult> TryReserveAsync(
        int productId,
        int quantity,
        InventoryOperationContext ctx,
        CancellationToken ct)
    {
        if (!ReservationsEnabled)
        {
            _log.LogDebug("inventory.reservation skipped disabled productId={P}", productId);
            return Task.FromResult(InventoryApplyResult.Success());
        }

        return Task.FromResult(InventoryApplyResult.Fail(
            "RESERVATIONS_NOT_ENABLED",
            "Reservas de inventario no están habilitadas en esta versión."));
    }

    public async Task<int> GetReservedQuantityAsync(int productId, CancellationToken ct)
    {
        if (!ReservationsEnabled)
            return 0;

        return await _db.InventoryReservations
            .Where(r => r.ProductId == productId && r.State == InventoryReservationState.Pending)
            .SumAsync(r => r.Quantity, ct);
    }
}
