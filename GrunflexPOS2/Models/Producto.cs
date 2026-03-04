namespace GrunflexPOS2.Models
{
    public class Producto
    {
        public int Id { get; set; }

        public string Nombre { get; set; } = string.Empty;

        // 🔥 costo del producto
        public decimal Costo { get; set; }

        // 🔥 precio de venta
        public decimal Precio { get; set; }

        public int Stock { get; set; }
    }
}