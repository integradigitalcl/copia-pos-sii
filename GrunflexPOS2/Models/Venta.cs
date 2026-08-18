using System;
using System.Collections.Generic;
using System.Linq;

// 🔥 ESTE USING ES EL QUE FALTA
using GrunflexPOS2.Models.Entities;

namespace GrunflexPOS2.Models
{
    public class Venta
    {
        public int NumeroTicket { get; set; }

        public DateTime Fecha { get; set; }

        public decimal Total { get; set; }

        // 🔥 EXISTENTE — multicaja
        public int NumeroCaja { get; set; } = 1;

        public Guid CajaId { get; set; }

        // 🔥 EXISTENTE (se mantiene por compatibilidad)
        public string Cajero { get; set; } = "Administrador";

        // 🔑 NUEVO — trazabilidad real
        public Guid UsuarioId { get; set; }

        public Guid CajaSesionId { get; set; }

        public string Cliente { get; set; } = "Público en general";

        public string MetodoPago { get; set; } = "Efectivo";

        public List<DetalleVenta> Items { get; set; } = new();

        // 🔥 Estado profesional
        public bool EstaAnulada { get; set; } = false;

        public DateTime? FechaAnulacion { get; set; }

        /// <summary>Salida de inventario sin registrar ingreso en caja ni totales de venta (consumo interno).</summary>
        public bool EsConsumoPersonal { get; set; }

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