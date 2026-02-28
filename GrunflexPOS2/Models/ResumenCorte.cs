using System;

namespace GrunflexPOS2.Models
{
    public class ResumenCorte
    {
        // 🔹 Información general
        public string NumeroCaja { get; set; } = string.Empty;
        public string Cajero { get; set; } = string.Empty;

        public DateTime FechaComercial { get; set; }
        public DateTime InicioPeriodo { get; set; }
        public DateTime FinPeriodo { get; set; }

        // 🔹 Fondo inicial
        public decimal MontoInicial { get; set; }

        // 🔹 Ventas generales
        public decimal TotalVentas { get; set; }
        public int TotalVentasRealizadas { get; set; }
        public int TotalArticulosVendidos { get; set; }

        // 🔹 Métodos de pago
        public decimal TotalEfectivo { get; set; }
        public decimal TotalDebito { get; set; }
        public decimal TotalCredito { get; set; }
        public decimal TotalTransferencia { get; set; }

        // 🔹 Cancelaciones / Devoluciones
        public decimal TotalAnuladas { get; set; }
        public decimal TotalDevoluciones { get; set; }

        // 🔹 Movimientos manuales de caja
        public decimal TotalEntradas { get; set; }
        public decimal TotalSalidas { get; set; }

        // 🔹 Impuestos y utilidad
        public decimal TotalImpuestos { get; set; }
        public decimal TotalGanancia { get; set; }

        // 🔹 Cálculo de caja
        public decimal DineroEsperadoEnCaja { get; set; }
        public decimal DineroRealEnCaja { get; set; }

        public decimal Diferencia => DineroRealEnCaja - DineroEsperadoEnCaja;

        // 🔹 Datos de cierre
        public DateTime FechaCierre { get; set; }
        public string UsuarioCierre { get; set; } = string.Empty;
    }
}