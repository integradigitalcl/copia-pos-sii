using System.Collections.Generic;

namespace GrunflexPOS2.Models.Entities
{
    public class Categoria
    {
        public int Id { get; set; }

        public string Nombre { get; set; } = string.Empty;

        public List<Producto> Productos { get; set; } = new List<Producto>();
    }
}