using System;
using System.Collections.Generic;
using System.Linq;

namespace GrunflexPOS2.Models
{
    public class Venta
    {
        public int NumeroTicket { get; set; }

        public DateTime Fecha { get; set; }

        public decimal Total { get; set; }

        // 🔥 NUEVO — Soporte multicaja
        public int NumeroCaja { get; set; } = 1;

        public string Cajero { get; set; } = "Administrador";

        public string Cliente { get; set; } = "Público en general";

        public string MetodoPago { get; set; } = "Efectivo";

        public List<DetalleVenta> Items { get; set; } = new();

        // 🔥 Estado profesional
        public bool EstaAnulada { get; set; } = false;

        public DateTime? FechaAnulacion { get; set; }

        // ================= PROPIEDADES CALCULADAS =================

        public int TotalArticulos
        {
            get
            {
                return Items?.Sum(i => i.Cantidad) ?? 0;
            }
        }

        public string Hora
        {
            get
            {
                return Fecha.ToString("HH:mm");
            }
        }
    }
}