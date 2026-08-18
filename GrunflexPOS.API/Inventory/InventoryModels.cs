namespace GrunflexPOS.API.Inventory;

/// <summary>Stock autoritativo por producto (1:1 con <see cref="Commerce.CommerceProducto"/>).</summary>
public sealed class InventoryStock
{
    public int ProductId { get; set; }
    public int QuantityOnHand { get; set; }
    public int ReservedQuantity { get; set; }
    public decimal AverageUnitCost { get; set; }
    public long RowVersion { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class InventoryMovement
{
    public Guid Id { get; set; }
    public int ProductId { get; set; }
    public InventoryMovementType MovementType { get; set; }
    public int QuantityDelta { get; set; }
    public int QuantityAfter { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TotalCost { get; set; }
    public decimal AverageUnitCostAfter { get; set; }
    public InventoryReferenceType ReferenceType { get; set; }
    public string? ReferenceId { get; set; }
    public Guid? TerminalId { get; set; }
    public string? TerminalCode { get; set; }
    public Guid? UserId { get; set; }
    public Guid? UserSessionId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? CajaId { get; set; }
    public string? RequestId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Preparación futura: reservas optimistas / carrito (no habilitado en runtime).</summary>
public sealed class InventoryReservation
{
    public Guid Id { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public InventoryReservationState State { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public Guid? TerminalId { get; set; }
    public string? ReferenceId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public enum InventoryReservationState
{
    Pending = 0,
    Committed = 1,
    Released = 2,
    Expired = 3
}

public enum InventoryMovementType
{
    Sale = 0,
    SaleReturn = 1,
    Purchase = 2,
    TransferIn = 3,
    TransferOut = 4,
    Adjustment = 5,
    VoidSale = 6,
    ReservationHold = 7,
    ReservationRelease = 8
}

public enum InventoryReferenceType
{
    Venta = 0,
    Devolucion = 1,
    Anulacion = 2,
    Compra = 3,
    Ajuste = 4,
    Transferencia = 5,
    Cierre = 6
}

/// <summary>Contexto operacional multicaja para auditoría de movimientos.</summary>
public sealed class InventoryOperationContext
{
    public Guid CajaId { get; init; }
    public Guid CajaSesionId { get; init; }
    public Guid UserId { get; init; }
    public Guid? TerminalId { get; init; }
    public string? TerminalCode { get; init; }
    public Guid? UserSessionId { get; init; }
    public Guid? BranchId { get; init; }
    public string? RequestId { get; init; }
}

public sealed class InventoryLineChange
{
    public int ProductId { get; init; }
    public int QuantityDelta { get; init; }
    public decimal? UnitCostForInbound { get; init; }
}

public sealed class InventoryStockChangeRequest
{
    public required InventoryOperationContext Operation { get; init; }
    public required IReadOnlyList<InventoryLineChange> Lines { get; init; }
    public InventoryMovementType MovementType { get; init; }
    public InventoryReferenceType ReferenceType { get; init; }
    public string? ReferenceId { get; init; }
    public bool AllowNegativeStock { get; init; }
}

public sealed class InventoryApplyResult
{
    public bool Ok { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
    public int? FailedProductId { get; init; }

    public static InventoryApplyResult Success() => new() { Ok = true };

    public static InventoryApplyResult Fail(string code, string msg, int? productId = null) =>
        new() { Ok = false, ErrorCode = code, Error = msg, FailedProductId = productId };
}

/// <summary>Fila bloqueada con token de concurrencia (xmin en PostgreSQL, RowVersion local).</summary>
public sealed class LockedInventoryRow
{
    public int ProductId { get; init; }
    public int QuantityOnHand { get; init; }
    public int ReservedQuantity { get; init; }
    public decimal AverageUnitCost { get; init; }
    public long RowVersion { get; init; }
    public uint? PostgresXmin { get; init; }
}
