using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GrunflexPOS2.Data;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS2.Services
{
    public sealed class VentaMetodoDto
    {
        public string Metodo { get; set; } = "";
        public decimal Total { get; set; }
    }

    public sealed class ProductoRankingDto
    {
        public string Producto { get; set; } = "";
        public int Cantidad { get; set; }
        public decimal Total { get; set; }
    }

    public sealed class VentaDiaDto
    {
        public string Etiqueta { get; set; } = "";
        public decimal Total { get; set; }
    }

    /// <summary>Reportes por día civil local (fechas almacenadas en UTC en la base SQLite).</summary>
    public class ReporteService
    {
        private readonly GrunflexDbContext _db;
        private readonly CultureInfo _cl = new CultureInfo("es-CL");

        public ReporteService(GrunflexDbContext? db = null)
        {
            _db = db ?? global::GrunflexPOS2.App.DbContext
                ?? throw new InvalidOperationException(
                    "Sin base de datos: use ReporteService(contexto) o inicie la app POS.");
        }

        private static DateTime InicioDiaLocal(DateTime d) => d.Date;

        private static DateTime FinDiaExclusivo(DateTime d) => d.Date.AddDays(1);

        /// <summary>Normaliza a UTC para comparar con la fecha de cada venta persistida en UTC.</summary>
        private static DateTime AUtc(DateTime limiteLocal)
        {
            if (limiteLocal.Kind == DateTimeKind.Utc)
                return DateTime.SpecifyKind(limiteLocal, DateTimeKind.Utc);
            if (limiteLocal.Kind == DateTimeKind.Local)
                return DateTime.SpecifyKind(limiteLocal.ToUniversalTime(), DateTimeKind.Utc);
            return DateTime.SpecifyKind(
                DateTime.SpecifyKind(limiteLocal, DateTimeKind.Local).ToUniversalTime(),
                DateTimeKind.Utc);
        }

        /// <summary>Ventas con fecha en [inicio, fin) interpretado en hora local, comparado en UTC.</summary>
        public decimal ObtenerTotalVentasRango(DateTime inicio, DateTime finExclusivo)
        {
            var u0 = AUtc(inicio);
            var u1 = AUtc(finExclusivo);
            return _db.Ventas
                .AsNoTracking()
                .Where(v => v.Fecha >= u0 && v.Fecha < u1 && !v.EstaAnulada)
                .Select(v => v.Total)
                .AsEnumerable()
                .Sum();
        }

        public int ObtenerCantidadVentasRango(DateTime inicio, DateTime finExclusivo)
        {
            var u0 = AUtc(inicio);
            var u1 = AUtc(finExclusivo);
            return _db.Ventas
                .AsNoTracking()
                .Count(v => v.Fecha >= u0 && v.Fecha < u1 && !v.EstaAnulada);
        }

        public decimal ObtenerVentasHoy()
        {
            var hoy = DateTime.Today;
            return ObtenerTotalVentasRango(InicioDiaLocal(hoy), FinDiaExclusivo(hoy));
        }

        public int ObtenerCantidadVentasHoy()
        {
            var hoy = DateTime.Today;
            return ObtenerCantidadVentasRango(InicioDiaLocal(hoy), FinDiaExclusivo(hoy));
        }

        public decimal ObtenerVentasTotales()
        {
            return _db.Ventas
                .AsNoTracking()
                .Where(v => !v.EstaAnulada)
                .Select(v => v.Total)
                .AsEnumerable()
                .Sum();
        }

        public List<VentaDiaDto> ObtenerVentasUltimos7Dias()
        {
            var hoy = DateTime.Today;
            var lista = new List<VentaDiaDto>();

            for (int i = 6; i >= 0; i--)
            {
                var dia = hoy.AddDays(-i);
                var inicio = InicioDiaLocal(dia);
                var fin = FinDiaExclusivo(dia);

                var total = ObtenerTotalVentasRango(inicio, fin);
                string nombreDia = _cl.DateTimeFormat.AbbreviatedDayNames[(int)dia.DayOfWeek];

                lista.Add(new VentaDiaDto
                {
                    Etiqueta = $"{nombreDia} {dia:dd}",
                    Total = total
                });
            }

            return lista;
        }

        /// <summary>Barras por hora local (8–22) del día actual.</summary>
        public List<(int Hora, decimal Total)> ObtenerVentasPorHoraHoy()
        {
            var inicio = InicioDiaLocal(DateTime.Today);
            var fin = FinDiaExclusivo(DateTime.Today);
            var u0 = AUtc(inicio);
            var u1 = AUtc(fin);

            var ventas = _db.Ventas
                .AsNoTracking()
                .Where(v => v.Fecha >= u0 && v.Fecha < u1 && !v.EstaAnulada)
                .Select(v => new { v.Fecha, v.Total })
                .ToList();

            var porHora = ventas
                .GroupBy(v => v.Fecha.ToLocalTime().Hour)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Total));

            var resultado = new List<(int Hora, decimal Total)>();

            for (int h = 8; h <= 22; h++)
            {
                porHora.TryGetValue(h, out var t);
                resultado.Add((h, t));
            }

            return resultado;
        }

        public List<VentaMetodoDto> ObtenerVentasPorMetodoEnRango(DateTime inicio, DateTime finExclusivo)
        {
            var u0 = AUtc(inicio);
            var u1 = AUtc(finExclusivo);
            return _db.Ventas
                .AsNoTracking()
                .Where(v => v.Fecha >= u0 && v.Fecha < u1 && !v.EstaAnulada)
                .Select(v => new { Metodo = v.MetodoPago, v.Total })
                .AsEnumerable()
                .GroupBy(v => v.Metodo ?? "Sin método")
                .Select(g => new VentaMetodoDto
                {
                    Metodo = g.Key,
                    Total = g.Sum(v => v.Total)
                })
                .OrderByDescending(x => x.Total)
                .ToList();
        }

        public List<VentaMetodoDto> ObtenerVentasPorMetodoTodoElTiempo()
        {
            return _db.Ventas
                .AsNoTracking()
                .Where(v => !v.EstaAnulada)
                .Select(v => new { Metodo = v.MetodoPago, v.Total })
                .AsEnumerable()
                .GroupBy(v => v.Metodo ?? "Sin método")
                .Select(g => new VentaMetodoDto
                {
                    Metodo = g.Key,
                    Total = g.Sum(v => v.Total)
                })
                .OrderByDescending(x => x.Total)
                .ToList();
        }

        public List<ProductoRankingDto> ObtenerProductosMasVendidos(int top = 10, DateTime? desde = null)
        {
            var q = _db.DetalleVentas
                .AsNoTracking()
                .Include(d => d.Venta)
                .Where(d => !d.Venta.EstaAnulada);

            if (desde.HasValue)
            {
                var uDesde = AUtc(InicioDiaLocal(desde.Value));
                q = q.Where(d => d.Venta.Fecha >= uDesde);
            }

            return q
                .Select(d => new { d.Producto, d.Cantidad, d.Precio })
                .AsEnumerable()
                .GroupBy(d => d.Producto)
                .Select(g => new ProductoRankingDto
                {
                    Producto = g.Key,
                    Cantidad = g.Sum(x => x.Cantidad),
                    Total = g.Sum(x => x.Cantidad * x.Precio)
                })
                .OrderByDescending(x => x.Cantidad)
                .Take(top)
                .ToList();
        }

        /// <summary>Suma de unidades (cantidades en líneas de detalle) en el rango local.</summary>
        public int ObtenerUnidadesVendidasRango(DateTime inicio, DateTime finExclusivo)
        {
            var u0 = AUtc(inicio);
            var u1 = AUtc(finExclusivo);
            return _db.DetalleVentas
                .AsNoTracking()
                .Join(
                    _db.Ventas.AsNoTracking().Where(v => !v.EstaAnulada),
                    d => d.VentaId,
                    v => v.Id,
                    (d, v) => new { d.Cantidad, v.Fecha })
                .Where(x => x.Fecha >= u0 && x.Fecha < u1)
                .Sum(x => x.Cantidad);
        }

        /// <summary>Cantidad de días civiles distintos con al menos una venta en el rango.</summary>
        public int ObtenerDiasConVentasEnRango(DateTime inicio, DateTime finExclusivo)
        {
            var u0 = AUtc(inicio);
            var u1 = AUtc(finExclusivo);
            return _db.Ventas
                .AsNoTracking()
                .Where(v => !v.EstaAnulada && v.Fecha >= u0 && v.Fecha < u1)
                .AsEnumerable()
                .Select(v => v.Fecha.ToLocalTime().Date)
                .Distinct()
                .Count();
        }
    }
}
