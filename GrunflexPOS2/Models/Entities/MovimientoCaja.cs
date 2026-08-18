using System;

namespace GrunflexPOS2.Models.Entities
{
    public class MovimientoCaja
    {
        public Guid Id { get; set; }

        public Guid CajaSesionId { get; set; }

        public DateTime Fecha { get; set; }

        public string Tipo { get; set; } = "";
        // VENTA / INGRESO / RETIRO

        public decimal Monto { get; set; }

        public string Descripcion { get; set; } = "";
    }
}