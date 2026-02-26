namespace GrunflexPOS2.Models
{
    public class DetalleVenta
    {
        public string Producto { get; set; } = string.Empty;

        public int Cantidad { get; set; }

        public decimal Precio { get; set; }

        public decimal Importe
        {
            get
            {
                return Cantidad * Precio;
            }
        }
    }
}
