namespace GrunflexPOS.API.Idempotency;

/// <summary>Tipos de operación crítica protegidos por <see cref="IIdempotencyService"/>.</summary>
public enum IdempotencyOperationType
{
    VentaCommit,
    VentaAnular,
    VentaDevolucionLinea,
    CajaCierre,
    CajaMovimiento,
    InventarioAjuste,
    Pago
}

public static class IdempotencyOperationTypeExtensions
{
    public static string ToStorageName(this IdempotencyOperationType type) => type switch
    {
        IdempotencyOperationType.VentaCommit => "venta.commit",
        IdempotencyOperationType.VentaAnular => "venta.anular",
        IdempotencyOperationType.VentaDevolucionLinea => "venta.devolucion_linea",
        IdempotencyOperationType.CajaCierre => "caja.cierre",
        IdempotencyOperationType.CajaMovimiento => "caja.movimiento",
        IdempotencyOperationType.InventarioAjuste => "inventario.ajuste",
        IdempotencyOperationType.Pago => "pago",
        _ => type.ToString()
    };
}
