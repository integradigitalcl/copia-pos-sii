using GrunflexPOS.API.Data;

namespace GrunflexPOS.API.Inventory;

public sealed class InventoryMovementService
{
    private readonly PosCommerceDbContext _db;
    private readonly ILogger<InventoryMovementService> _log;

    public InventoryMovementService(PosCommerceDbContext db, ILogger<InventoryMovementService> log)
    {
        _db = db;
        _log = log;
    }

    public void RecordMovement(
        int productId,
        InventoryMovementType movementType,
        int quantityDelta,
        int quantityAfter,
        decimal unitCost,
        decimal averageAfter,
        InventoryReferenceType referenceType,
        string? referenceId,
        InventoryOperationContext ctx)
    {
        var totalCost = Math.Abs(quantityDelta) * unitCost;
        var movement = new InventoryMovement
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            MovementType = movementType,
            QuantityDelta = quantityDelta,
            QuantityAfter = quantityAfter,
            UnitCost = unitCost,
            TotalCost = totalCost,
            AverageUnitCostAfter = averageAfter,
            ReferenceType = referenceType,
            ReferenceId = referenceId,
            TerminalId = ctx.TerminalId,
            TerminalCode = ctx.TerminalCode,
            UserId = ctx.UserId,
            UserSessionId = ctx.UserSessionId,
            BranchId = ctx.BranchId,
            CajaId = ctx.CajaId,
            RequestId = ctx.RequestId,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.InventoryMovements.Add(movement);
        _log.LogInformation(
            "inventory.movement type={Type} productId={P} delta={D} after={A} ref={Rt}/{Rid} terminal={T}",
            movementType, productId, quantityDelta, quantityAfter, referenceType, referenceId, ctx.TerminalId);
    }
}
