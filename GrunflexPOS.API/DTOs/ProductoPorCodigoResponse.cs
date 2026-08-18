namespace GrunflexPOS.API.DTOs;

public sealed class ProductoPorCodigoResponse
{
    public int Id { get; set; }
    public string CodigoBarras { get; set; } = "";
    public string Nombre { get; set; } = "";
    public decimal Precio { get; set; }
    public int Stock { get; set; }
}
