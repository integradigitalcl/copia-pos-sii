using System;

namespace GrunflexPOS2.Models
{
    public class SesionCaja
    {
        public int NumeroCaja { get; set; }

        public string Cajero { get; set; } = string.Empty;

        public DateTime FechaApertura { get; set; }

        public decimal MontoInicial { get; set; }

        public bool Activa { get; set; }
    }
}