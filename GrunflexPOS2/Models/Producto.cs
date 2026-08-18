using System.ComponentModel.DataAnnotations;

namespace GrunflexPOS2.Models.Entities
{
    public class Producto
    {
        public int Id { get; set; }

        public string Nombre { get; set; } = string.Empty;

        public decimal Costo { get; set; }

        public decimal Precio { get; set; }

        public int Stock { get; set; }

        [MaxLength(50)]
        public string CodigoBarras { get; set; } = string.Empty; // 🔥 evita null

        /// <summary>P. Mayoreo del catálogo / Excel.</summary>
        public decimal PrecioMayoreo { get; set; }

        public int InvMinimo { get; set; }

        public int InvMaximo { get; set; }

        [MaxLength(50)]
        public string TipoVenta { get; set; } = "";

        [MaxLength(120)]
        public string Departamento { get; set; } = "";

        public int? CategoriaId { get; set; }

        public Categoria? Categoria { get; set; } // 🔥 nullable para evitar errores EF
    }
}