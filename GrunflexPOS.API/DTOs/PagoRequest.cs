namespace GrunflexPOS.API.DTOs
{
    public class PagoRequest
    {
        public decimal Monto { get; set; }

        public string NumeroTicket { get; set; } = string.Empty;

        // 🔴 IDENTIDAD
        public string TerminalId { get; set; } = string.Empty;

        // 🔴 SEGURIDAD (NUEVO)
        public string Token { get; set; } = string.Empty;
    }
}