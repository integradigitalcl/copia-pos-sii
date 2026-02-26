using GrunflexPOS2.Models;

namespace GrunflexPOS2.Services
{
    public static class CajaService
    {
        public static SesionCaja? SesionActual { get; private set; }

        public static void AbrirCaja(int numeroCaja, string cajero, decimal montoInicial)
        {
            SesionActual = new SesionCaja
            {
                NumeroCaja = numeroCaja,
                Cajero = cajero,
                FechaApertura = System.DateTime.Now,
                MontoInicial = montoInicial,
                Activa = true
            };
        }

        public static void CerrarCaja()
        {
            if (SesionActual != null)
                SesionActual.Activa = false;
        }

        public static bool CajaAbierta()
        {
            return SesionActual != null && SesionActual.Activa;
        }
    }
}