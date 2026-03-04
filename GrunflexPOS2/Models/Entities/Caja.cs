using System;

namespace GrunflexPOS2.Models.Entities
{
    public class Caja
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Nombre { get; set; } = string.Empty;

        public Guid EmpresaId { get; set; }

        // 🔥 Relación con Empresa
        public Empresa Empresa { get; set; } = null!;

        public bool Activa { get; set; } = true;

        public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
    }
}