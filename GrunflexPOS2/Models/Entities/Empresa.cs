using System;
using System.Collections.Generic;

namespace GrunflexPOS2.Models.Entities
{
    public class Empresa
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Nombre { get; set; } = string.Empty;

        public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

        // 🔥 Relación con Cajas
        public ICollection<Caja> Cajas { get; set; } = new List<Caja>();
    }
}