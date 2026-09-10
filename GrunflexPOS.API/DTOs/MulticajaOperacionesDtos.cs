namespace GrunflexPOS.API.DTOs;

public sealed class MulticajaVentaLineaDto
{
    /// <summary>Id del producto en la base central, cuando la caja ya lo conoce.</summary>
    public int? ProductoId { get; set; }

    public string? CodigoBarras { get; set; }
    public string Producto { get; set; } = "";
    public int Cantidad { get; set; }
    public decimal Precio { get; set; }

    /// <summary>Datos para crear el producto en la central si aún no existe.</summary>
    public decimal? Costo { get; set; }
    public int? Stock { get; set; }
    public string? Departamento { get; set; }
    public string? TipoVenta { get; set; }

    /// <summary>
    /// Componentes de una promoción. Si hay ítems, el inventario se descuenta por producto
    /// y el stock de la promoción se alinea a kits disponibles.
    /// </summary>
    public List<MulticajaVentaComponenteDto>? Componentes { get; set; }
}

public sealed class MulticajaVentaComponenteDto
{
    public int? ProductoId { get; set; }
    public string? CodigoBarras { get; set; }
    public string? Producto { get; set; }
    /// <summary>Unidades del producto por cada kit de promoción vendido.</summary>
    public int CantidadPorKit { get; set; }
}

public sealed class MulticajaProductoUpsertResponse
{
    public bool Ok { get; set; }
    public int Upserted { get; set; }
    public string? Error { get; set; }
}

public sealed class MulticajaVentaCommitRequest
{
    /// <summary>Idempotencia at-least-once (GUID recomendado).</summary>
    public string RequestId { get; set; } = "";

    public Guid CajaId { get; set; }
    public Guid CajaSesionId { get; set; }
    public Guid UsuarioId { get; set; }

    /// <summary>Terminal multicaja (auditoría inventario).</summary>
    public Guid? TerminalId { get; set; }
    public string? TerminalCode { get; set; }
    public Guid? UserSessionId { get; set; }
    public Guid? BranchId { get; set; }

    public string Cliente { get; set; } = "Público en general";
    public string MetodoPago { get; set; } = "Efectivo";
    public bool EsConsumoPersonal { get; set; }

    /// <summary>Porción en efectivo (Mixto). Ignorado si el método no es Mixto.</summary>
    public decimal? MontoEfectivo { get; set; }

    public List<MulticajaVentaLineaDto> Items { get; set; } = new();
}

public sealed class MulticajaVentaCommitResponse
{
    public bool Ok { get; set; }
    public int NumeroTicket { get; set; }
    public Guid VentaId { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class MulticajaCajaSesionAbrirRequest
{
    public Guid CajaId { get; set; }
    public Guid UsuarioId { get; set; }
    public string Username { get; set; } = "";
    public decimal MontoInicial { get; set; }
}

public sealed class MulticajaCajaSesionDto
{
    public Guid Id { get; set; }
    public Guid CajaId { get; set; }
    public int NumeroCaja { get; set; }
    public string Cajero { get; set; } = "";
    public Guid UsuarioAperturaId { get; set; }
    public DateTime FechaApertura { get; set; }
    public decimal MontoApertura { get; set; }
    public bool Abierta { get; set; }
    public decimal TotalVentas { get; set; }
    public decimal TotalIngresos { get; set; }
    public decimal TotalRetiros { get; set; }
}

public sealed class MulticajaLoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class MulticajaLoginResponse
{
    public bool Ok { get; set; }
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Rol { get; set; } = "";
    public string? Error { get; set; }
}

public sealed class MulticajaCajaAutoRegistroRequest
{
    public string MachineName { get; set; } = "";
}

public sealed class MulticajaCajaAutoRegistroResponse
{
    public bool Ok { get; set; }
    public Guid CajaId { get; set; }
    public string Nombre { get; set; } = "";
    public string? Error { get; set; }
}

// --- Anulación / devolución / cierre (multicaja central) ---

public sealed class MulticajaAnularVentaRequest
{
    public string RequestId { get; set; } = "";
    public Guid CajaId { get; set; }
    public Guid CajaSesionId { get; set; }
    public Guid UsuarioId { get; set; }
    public Guid? TerminalId { get; set; }
    public string? TerminalCode { get; set; }
    public Guid? UserSessionId { get; set; }
    public Guid? BranchId { get; set; }
    public int NumeroTicket { get; set; }
}

public sealed class MulticajaAnularVentaResponse
{
    public bool Ok { get; set; }
    public int NumeroTicket { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class MulticajaDevolucionLineaRequest
{
    public string RequestId { get; set; } = "";
    public Guid CajaId { get; set; }
    public Guid CajaSesionId { get; set; }
    public Guid UsuarioId { get; set; }
    public Guid? TerminalId { get; set; }
    public string? TerminalCode { get; set; }
    public Guid? UserSessionId { get; set; }
    public Guid? BranchId { get; set; }
    public int NumeroTicket { get; set; }
    public string? CodigoBarras { get; set; }
    public string Producto { get; set; } = "";
    public decimal Precio { get; set; }
    public int Cantidad { get; set; }
}

public sealed class MulticajaDevolucionLineaResponse
{
    public bool Ok { get; set; }
    public int NumeroTicket { get; set; }
    public decimal MontoDevuelto { get; set; }
    public decimal NuevoTotalVenta { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class MulticajaCierreCajaRequest
{
    public string RequestId { get; set; } = "";
    public Guid CajaId { get; set; }
    public Guid CajaSesionId { get; set; }
    public Guid UsuarioCierreId { get; set; }
    public decimal MontoContado { get; set; }
}

public sealed class MulticajaCierreCajaResponse
{
    public bool Ok { get; set; }
    public decimal Esperado { get; set; }
    public decimal MontoContado { get; set; }
    public decimal Diferencia { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class MulticajaMovimientoCajaRequest
{
    public string RequestId { get; set; } = "";
    public Guid CajaId { get; set; }
    public Guid CajaSesionId { get; set; }
    public Guid UsuarioId { get; set; }
    /// <summary>INGRESO o RETIRO.</summary>
    public string Tipo { get; set; } = "";
    public decimal Monto { get; set; }
    public string Descripcion { get; set; } = "";
}

public sealed class MulticajaMovimientoCajaResponse
{
    public bool Ok { get; set; }
    public string? Tipo { get; set; }
    public decimal Monto { get; set; }
    public decimal TotalIngresos { get; set; }
    public decimal TotalRetiros { get; set; }
    public decimal TotalVentas { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}

// --- Catálogo / usuarios (sincronización caja adicional API-only) ---

public sealed class MulticajaUsuarioDto
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Rol { get; set; } = "";
}

public sealed class MulticajaUsuarioUpsertRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Rol { get; set; } = "";
}

public sealed class MulticajaProductoDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public decimal Costo { get; set; }
    public decimal Precio { get; set; }
    public int Stock { get; set; }
    public string CodigoBarras { get; set; } = "";
    public decimal PrecioMayoreo { get; set; }
    public int InvMinimo { get; set; }
    public int InvMaximo { get; set; }
    public string TipoVenta { get; set; } = "";
    public string Departamento { get; set; } = "";
    public int? CategoriaId { get; set; }
}

public sealed class MulticajaInventarioAjusteRequest
{
    public string RequestId { get; set; } = "";
    public Guid CajaId { get; set; }
    public Guid CajaSesionId { get; set; }
    public Guid UsuarioId { get; set; }
    public Guid? TerminalId { get; set; }
    public string? TerminalCode { get; set; }
    public Guid? UserSessionId { get; set; }
    public Guid? BranchId { get; set; }
    public int ProductoId { get; set; }
    public int CantidadDelta { get; set; }
    public string Motivo { get; set; } = "";
}

public sealed class MulticajaInventarioAjusteResponse
{
    public bool Ok { get; set; }
    public int ProductoId { get; set; }
    public int StockAnterior { get; set; }
    public int StockNuevo { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}
