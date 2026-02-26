using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GrunflexPOS2.Models;

namespace GrunflexPOS2.Services
{
    public static class VentasService
    {
        private static int _ultimoNumeroTicket = 0;
        private static List<Venta> _historialVentas = new();

        private static readonly string _rutaArchivo =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GrunflexPOS",
                "ventas.json");

        static VentasService()
        {
            CargarVentas();
        }

        // ================= GENERAR NÚMERO DE TICKET =================
        public static int GenerarNumeroTicket()
        {
            _ultimoNumeroTicket++;
            return _ultimoNumeroTicket;
        }

        // ================= GUARDAR VENTA =================
        public static void GuardarVenta(Venta venta)
        {
            _historialVentas.Add(venta);
            GuardarEnArchivo();
        }

        // ================= OBTENER TODAS =================
        public static List<Venta> ObtenerVentas()
        {
            return _historialVentas;
        }

        // ================= ANULAR VENTA =================
        public static void AnularVenta(int numeroTicket)
        {
            var venta = _historialVentas
                .FirstOrDefault(v => v.NumeroTicket == numeroTicket);

            if (venta != null && !venta.EstaAnulada)
            {
                venta.EstaAnulada = true;
                venta.FechaAnulacion = DateTime.Now;
                GuardarEnArchivo();
            }
        }

        // ================= OBTENER POR TICKET =================
        public static Venta? ObtenerVentaPorTicket(int numeroTicket)
        {
            return _historialVentas
                .FirstOrDefault(v => v.NumeroTicket == numeroTicket);
        }

        // ================= GUARDAR EN JSON =================
        private static void GuardarEnArchivo()
        {
            try
            {
                var carpeta = Path.GetDirectoryName(_rutaArchivo);

                if (!Directory.Exists(carpeta))
                    Directory.CreateDirectory(carpeta!);

                var json = JsonSerializer.Serialize(
                    _historialVentas,
                    new JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(_rutaArchivo, json);
            }
            catch
            {
                // Evita romper POS por error de archivo
            }
        }

        // ================= CARGAR JSON =================
        private static void CargarVentas()
        {
            try
            {
                if (!File.Exists(_rutaArchivo))
                    return;

                var json = File.ReadAllText(_rutaArchivo);

                var ventas = JsonSerializer.Deserialize<List<Venta>>(json);

                if (ventas != null)
                {
                    _historialVentas = ventas;

                    if (_historialVentas.Count > 0)
                        _ultimoNumeroTicket =
                            _historialVentas.Max(v => v.NumeroTicket);
                }
            }
            catch
            {
                _historialVentas = new List<Venta>();
            }
        }
    }
}
