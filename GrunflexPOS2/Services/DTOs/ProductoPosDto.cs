namespace GrunflexPOS2.Services.DTOs;

/// <summary>Producto devuelto por API para venta POS.</summary>
public sealed class ProductoPosDto
{
    public int Id { get; set; }
    public string CodigoBarras { get; set; } = "";
    public string Nombre { get; set; } = "";
    public decimal Precio { get; set; }
    public int Stock { get; set; }
}
