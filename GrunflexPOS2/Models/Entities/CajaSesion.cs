using System;

namespace GrunflexPOS2.Models.Entities
{
    public class CajaSesion
    {
        public Guid Id { get; set; }

        public Guid CajaId { get; set; }

        public int NumeroCaja { get; set; }
        public string Cajero { get; set; } = string.Empty;

        public Guid UsuarioAperturaId { get; set; }
        public Guid? UsuarioCierreId { get; set; }

        public DateTime FechaApertura { get; set; }

        public decimal MontoApertura { get; set; }

        public DateTime? FechaCierre { get; set; }

        public decimal? MontoCierre { get; set; }

        // 🔥 NUEVO (CLAVE PARA AUDITORÍA)
        public decimal Diferencia { get; set; }

        public decimal TotalVentas { get; set; }

        public decimal TotalIngresos { get; set; }

        public decimal TotalRetiros { get; set; }

        public bool Abierta { get; set; } = true;
    }
}