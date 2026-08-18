using System;

namespace GrunflexPOS2.Services
{
    public static class SoporteContexto
    {
        public static string Modulo { get; set; } = "";
        public static string Accion { get; set; } = "";
        public static string Producto { get; set; } = "";
        public static decimal Venta { get; set; } = 0;
        public static string Error { get; set; } = "";

        public static void Limpiar()
        {
            Modulo = "";
            Accion = "";
            Producto = "";
            Venta = 0;
            Error = "";
        }
    }
}