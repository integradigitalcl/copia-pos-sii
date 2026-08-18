using System;
using System.ComponentModel.DataAnnotations;

namespace GrunflexPOS2.Models.Entities
{
    public class DetalleVenta
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid VentaId { get; set; }

        public VentaEntity Venta { get; set; } = null!;

        [MaxLength(50)]
        public string? CodigoBarras { get; set; }

        public string Producto { get; set; } = "";

        public int Cantidad { get; set; }

        public decimal Precio { get; set; }

        // 🔥 SOLO ESTO SE AGREGA
        public decimal Importe => Cantidad * Precio;
    }
}