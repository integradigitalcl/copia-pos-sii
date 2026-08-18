namespace GrunflexPOS.API.Commerce;

public sealed class CommerceEmpresa
{
    public Guid Id { get; set; }
    public string Nombre { get; set; } = "";
    public DateTime FechaCreacion { get; set; }
}

public sealed class CommerceCaja
{
    public Guid Id { get; set; }
    public string Nombre { get; set; } = "";
    public Guid EmpresaId { get; set; }
    public bool Activa { get; set; }
    public DateTime FechaCreacion { get; set; }
}

public sealed class CommerceUsuario
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Rol { get; set; } = "";
}

public sealed class CommerceCajaSesion
{
    public Guid Id { get; set; }
    public Guid CajaId { get; set; }
    public int NumeroCaja { get; set; }
    public string Cajero { get; set; } = "";
    public Guid UsuarioAperturaId { get; set; }
    public Guid? UsuarioCierreId { get; set; }
    public DateTime FechaApertura { get; set; }
    public decimal MontoApertura { get; set; }
    public DateTime? FechaCierre { get; set; }
    public decimal? MontoCierre { get; set; }
    public decimal Diferencia { get; set; }
    public decimal TotalVentas { get; set; }
    public decimal TotalIngresos { get; set; }
    public decimal TotalRetiros { get; set; }
    public bool Abierta { get; set; }
}

public sealed class CommerceMovimientoCaja
{
    public Guid Id { get; set; }
    public Guid CajaSesionId { get; set; }
    public DateTime Fecha { get; set; }
    public string Tipo { get; set; } = "";
    public decimal Monto { get; set; }
    public string Descripcion { get; set; } = "";
}

public sealed class CommerceVenta
{
    public Guid Id { get; set; }
    public int NumeroTicket { get; set; }
    public DateTime Fecha { get; set; }
    public decimal Total { get; set; }
    public int NumeroCaja { get; set; }
    public Guid CajaId { get; set; }
    public string Cajero { get; set; } = "";
    public string Cliente { get; set; } = "";
    public string MetodoPago { get; set; } = "";
    public bool EstaAnulada { get; set; }
    public DateTime? FechaAnulacion { get; set; }
    public Guid UsuarioId { get; set; }
    public Guid CajaSesionId { get; set; }
    public bool EsConsumoPersonal { get; set; }
}

public sealed class CommerceDetalleVenta
{
    public Guid Id { get; set; }
    public Guid VentaId { get; set; }
    public string? CodigoBarras { get; set; }
    public string Producto { get; set; } = "";
    public int Cantidad { get; set; }
    public decimal Precio { get; set; }
}

public sealed class CommerceProducto
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
