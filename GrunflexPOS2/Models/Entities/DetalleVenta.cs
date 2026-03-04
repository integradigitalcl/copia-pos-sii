using System;

namespace GrunflexPOS2.Models.Entities
{
    public class DetalleVenta
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid VentaId { get; set; }

        public VentaEntity Venta { get; set; } = null!;

        public string Producto { get; set; } = "";

        public int Cantidad { get; set; }

        public decimal Precio { get; set; }
    }
}