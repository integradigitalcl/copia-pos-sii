using System;
using System.Collections.Generic;
using System.Linq;
using GrunflexPOS2.Models;

namespace GrunflexPOS2.Services
{
    public class CorteService
    {
        private readonly DiaComercialService _diaService;
        private readonly VentaService _ventaService;

        public CorteService(
            DiaComercialService diaService,
            VentaService ventaService)
        {
            _diaService = diaService;
            _ventaService = ventaService;
        }

        public ResumenCorte GenerarResumen(string numeroCaja, string cajero, decimal montoInicial)
        {
            var fechaComercial = _diaService.ObtenerFechaComercialActual();
            var rango = _diaService.ObtenerRangoDiaComercial(fechaComercial);

            var ventas = _ventaService.ObtenerVentas()
                .Where(v =>
                    v.NumeroCaja.ToString() == numeroCaja &&
                    v.Fecha >= rango.inicio &&
                    v.Fecha < rango.fin)
                .ToList();

            var ventasValidas = ventas.Where(v => !v.EstaAnulada).ToList();
            var ventasAnuladas = ventas.Where(v => v.EstaAnulada).ToList();

            decimal totalEntradas = 0;     // Futuro módulo movimientos
            decimal totalSalidas = 0;      // Futuro módulo movimientos
            decimal totalDevoluciones = ventasAnuladas.Sum(v => v.Total);

            decimal totalVentas = ventasValidas.Sum(v => v.Total);

            decimal totalEfectivo = ventasValidas
                .Where(v => v.MetodoPago == "Efectivo")
                .Sum(v => v.Total);

            decimal totalDebito = ventasValidas
                .Where(v => v.MetodoPago == "Debito")
                .Sum(v => v.Total);

            decimal totalCredito = ventasValidas
                .Where(v => v.MetodoPago == "Credito")
                .Sum(v => v.Total);

            decimal totalTransferencia = ventasValidas
                .Where(v => v.MetodoPago == "Transferencia")
                .Sum(v => v.Total);

            int totalArticulos = ventasValidas
                .Sum(v => v.Items.Sum(i => i.Cantidad));

            int totalVentasRealizadas = ventasValidas.Count;

            // 🔥 Preparado para futuro módulo de costos
            decimal totalGanancia = 0;

            // 🔥 Preparado para futuro módulo de impuestos
            decimal totalImpuestos = 0;

            var resumen = new ResumenCorte
            {
                NumeroCaja = numeroCaja,
                Cajero = cajero,
                FechaComercial = fechaComercial,
                InicioPeriodo = rango.inicio,
                FinPeriodo = rango.fin,
                MontoInicial = montoInicial,

                TotalVentas = totalVentas,
                TotalVentasRealizadas = totalVentasRealizadas,
                TotalArticulosVendidos = totalArticulos,

                TotalEfectivo = totalEfectivo,
                TotalDebito = totalDebito,
                TotalCredito = totalCredito,
                TotalTransferencia = totalTransferencia,

                TotalAnuladas = ventasAnuladas.Sum(v => v.Total),

                TotalEntradas = totalEntradas,
                TotalSalidas = totalSalidas,
                TotalDevoluciones = totalDevoluciones,

                TotalGanancia = totalGanancia,
                TotalImpuestos = totalImpuestos,

                DineroEsperadoEnCaja =
                    montoInicial
                    + totalEfectivo
                    + totalEntradas
                    - totalSalidas
                    - totalDevoluciones,

                FechaCierre = DateTime.Now,
                UsuarioCierre = cajero
            };

            return resumen;
        }
    }
}