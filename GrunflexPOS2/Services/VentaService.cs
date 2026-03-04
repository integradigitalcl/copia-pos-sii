using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GrunflexPOS2.Models;
using GrunflexPOS2.Models.Entities;

namespace GrunflexPOS2.Services
{
    public class VentaService
    {
        private List<Venta> _historialVentas = new();

        // 🔥 RUTA EN RED (MULTICAJA REAL)
        private readonly string _rutaArchivo =
            @"\\DESKTOP-7VI49G5\GrunflexPOS\ventas.json";

        public VentaService()
        {
            CargarVentas();
        }

        // ================= GENERAR NÚMERO DE TICKET (SEGURO EN RED) =================
        public int GenerarNumeroTicket()
        {
            lock (_historialVentas)
            {
                CargarVentas();

                int ultimoNumero = 0;

                if (_historialVentas.Count > 0)
                {
                    ultimoNumero = _historialVentas.Max(v => v.NumeroTicket);
                }

                return ultimoNumero + 1;
            }
        }

        // ================= GUARDAR VENTA =================
        public void GuardarVenta(Venta venta)
        {
            lock (_historialVentas)
            {
                CargarVentas();

                // 🔥 MULTICAJA AUTOMÁTICO
                if (CajaService.SesionActual != null)
                {
                    venta.NumeroCaja = CajaService.SesionActual.NumeroCaja;
                    venta.Cajero = CajaService.SesionActual.Cajero;
                }

                // 🔥 Asociar venta a la caja actual
                venta.CajaId = App.CajaActualId;

                // ================= GUARDAR JSON (SISTEMA ACTUAL) =================
                _historialVentas.Add(venta);
                GuardarEnArchivo();

                // ================= GUARDAR EN POSTGRESQL =================
                try
                {
                    var ventaDb = new VentaEntity
                    {
                        NumeroTicket = venta.NumeroTicket,
                        Fecha = venta.Fecha,
                        Total = venta.Total,
                        NumeroCaja = venta.NumeroCaja,
                        CajaId = venta.CajaId,
                        Cajero = venta.Cajero,
                        Cliente = venta.Cliente,
                        MetodoPago = venta.MetodoPago,
                        EstaAnulada = venta.EstaAnulada,
                        FechaAnulacion = venta.FechaAnulacion
                    };

                    App.DbContext.Ventas.Add(ventaDb);
                    App.DbContext.SaveChanges();
                }
                catch
                {
                    // 🔥 Si falla la base de datos el POS sigue funcionando
                }
            }
        }

        // ================= OBTENER TODAS =================
        public List<Venta> ObtenerVentas()
        {
            CargarVentas();
            return _historialVentas;
        }

        // ================= ANULAR VENTA =================
        public void AnularVenta(int numeroTicket)
        {
            lock (_historialVentas)
            {
                CargarVentas();

                var venta = _historialVentas
                    .FirstOrDefault(v => v.NumeroTicket == numeroTicket);

                if (venta != null && !venta.EstaAnulada)
                {
                    venta.EstaAnulada = true;
                    venta.FechaAnulacion = DateTime.Now;

                    GuardarEnArchivo();

                    try
                    {
                        var ventaDb = App.DbContext.Ventas
                            .FirstOrDefault(v => v.NumeroTicket == numeroTicket);

                        if (ventaDb != null)
                        {
                            ventaDb.EstaAnulada = true;
                            ventaDb.FechaAnulacion = venta.FechaAnulacion;

                            App.DbContext.SaveChanges();
                        }
                    }
                    catch
                    {
                        // No romper POS
                    }
                }
            }
        }

        // ================= OBTENER POR TICKET =================
        public Venta? ObtenerVentaPorTicket(int numeroTicket)
        {
            CargarVentas();

            return _historialVentas
                .FirstOrDefault(v => v.NumeroTicket == numeroTicket);
        }

        // ================= GUARDAR EN JSON =================
        private void GuardarEnArchivo()
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
        private void CargarVentas()
        {
            try
            {
                if (!File.Exists(_rutaArchivo))
                {
                    _historialVentas = new List<Venta>();
                    return;
                }

                var json = File.ReadAllText(_rutaArchivo);

                var ventas = JsonSerializer.Deserialize<List<Venta>>(json);

                _historialVentas = ventas ?? new List<Venta>();
            }
            catch
            {
                _historialVentas = new List<Venta>();
            }
        }
    }
}