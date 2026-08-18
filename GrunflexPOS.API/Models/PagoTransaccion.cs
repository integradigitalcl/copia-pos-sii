using System;

namespace GrunflexPOS.API.Models
{
    public class PagoTransaccion
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public decimal Monto { get; set; }

        public string NumeroTicket { get; set; } = "";

        public string Estado { get; set; } = "PENDIENTE";
        // PENDIENTE | APROBADO | RECHAZADO

        public string CodigoAutorizacion { get; set; } = "";

        public DateTime FechaCreacion { get; set; } = DateTime.Now;

        // 🔴 IDENTIDAD
        public string TerminalId { get; set; } = "";

        // 🔴 SEGURIDAD (NUEVO)
        public string Token { get; set; } = "";
    }
}