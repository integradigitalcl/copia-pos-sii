using System;
using System.Collections.Generic;
using System.Linq;
using GrunflexPOS2.Data;

namespace GrunflexPOS2.Services
{
    public class ReporteService
    {
        private readonly GrunflexDbContext _db;

        public ReporteService()
        {
            _db = App.DbContext;
        }

        public decimal ObtenerVentasHoy()
        {
            var hoy = DateTime.UtcNow.Date;

            return _db.Ventas
                .Where(v => v.Fecha.Date == hoy && !v.EstaAnulada)
                .Sum(v => (decimal?)v.Total) ?? 0;
        }

        public int ObtenerCantidadVentasHoy()
        {
            var hoy = DateTime.UtcNow.Date;

            return _db.Ventas
                .Count(v => v.Fecha.Date == hoy && !v.EstaAnulada);
        }

        public decimal ObtenerVentasTotales()
        {
            return _db.Ventas
                .Where(v => !v.EstaAnulada)
                .Sum(v => (decimal?)v.Total) ?? 0;
        }

        // =========================
        // VENTAS ÚLTIMOS 7 DÍAS
        // =========================

        public List<(string Dia, decimal Total)> ObtenerVentasUltimos7Dias()
        {
            var hoy = DateTime.UtcNow.Date;

            List<(string Dia, decimal Total)> ventas = new();

            for (int i = 6; i >= 0; i--)
            {
                var dia = hoy.AddDays(-i);

                var total = _db.Ventas
                    .Where(v => v.Fecha.Date == dia && !v.EstaAnulada)
                    .Sum(v => (decimal?)v.Total) ?? 0;

                ventas.Add((dia.ToString("ddd"), total));
            }

            return ventas;
        }

        // =========================
        // VENTAS POR MÉTODO DE PAGO
        // =========================

        public List<dynamic> ObtenerVentasPorMetodo()
        {
            return _db.Ventas
                .Where(v => !v.EstaAnulada)
                .GroupBy(v => v.MetodoPago)
                .Select(g => new
                {
                    Metodo = g.Key,
                    Total = g.Sum(v => v.Total)
                })
                .ToList<dynamic>();
        }

        // =========================
        // VENTAS POR CAJA
        // =========================

        public List<dynamic> ObtenerVentasPorCaja()
        {
            return _db.Ventas
                .Where(v => !v.EstaAnulada)
                .GroupBy(v => v.NumeroCaja)
                .Select(g => new
                {
                    Caja = g.Key,
                    Total = g.Sum(v => v.Total)
                })
                .ToList<dynamic>();
        }

        // =========================
        // PRODUCTOS MÁS VENDIDOS
        // =========================

        public List<dynamic> ObtenerProductosMasVendidos()
        {
            return _db.DetalleVentas
                .GroupBy(d => d.Producto)
                .Select(g => new
                {
                    Producto = g.Key,
                    Cantidad = g.Sum(x => x.Cantidad),
                    Total = g.Sum(x => x.Cantidad * x.Precio)
                })
                .OrderByDescending(x => x.Cantidad)
                .Take(10)
                .ToList<dynamic>();
        }
    }
}