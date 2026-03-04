using System;
using System.Collections.Generic;

namespace GrunflexPOS2.Models.Entities
{
    public class VentaEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public int NumeroTicket { get; set; }

        public DateTime Fecha { get; set; }

        // 🔥 RELACIÓN CON DETALLEVENTA
        public ICollection<DetalleVenta> Items { get; set; } = new List<DetalleVenta>();

        public decimal Total { get; set; }

        public int NumeroCaja { get; set; }

        public Guid CajaId { get; set; }

        public string Cajero { get; set; } = "";

        public string Cliente { get; set; } = "";

        public string MetodoPago { get; set; } = "";

        public bool EstaAnulada { get; set; }

        public DateTime? FechaAnulacion { get; set; }
    }
}