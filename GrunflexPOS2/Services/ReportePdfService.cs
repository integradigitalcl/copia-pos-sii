using System;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using GrunflexPOS2.Data;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GrunflexPOS2.Services
{
    public static class ReportePdfService
    {
        /// <param name="db">Contexto EF; null usa el DbContext global del POS.</param>
        public static void GenerarReportePDF(
            GrunflexDbContext? db = null,
            DateTime? periodoInicio = null,
            DateTime? periodoFinExclusivo = null,
            string? tituloPeriodo = null)
        {
            try
            {
                var cl = new CultureInfo("es-CL");
                var reportes = new ReporteService(db);
                var hoy = DateTime.Today;
                var inicioHoy = hoy.Date;
                var finHoy = hoy.AddDays(1);
                var inicioMes = new DateTime(hoy.Year, hoy.Month, 1);
                var finMes = inicioMes.AddMonths(1);
                var inicioPeriodo = periodoInicio ?? inicioMes;
                var finPeriodo = periodoFinExclusivo ?? finMes;
                var titulo = string.IsNullOrWhiteSpace(tituloPeriodo)
                    ? "Mes actual"
                    : tituloPeriodo;

                var totalHoy = reportes.ObtenerTotalVentasRango(inicioHoy, finHoy);
                var cantHoy = reportes.ObtenerCantidadVentasRango(inicioHoy, finHoy);
                var ticket = cantHoy > 0 ? totalHoy / cantHoy : 0;
                var totalPeriodo = reportes.ObtenerTotalVentasRango(inicioPeriodo, finPeriodo);
                var cantPeriodo = reportes.ObtenerCantidadVentasRango(inicioPeriodo, finPeriodo);

                var ayer = hoy.AddDays(-1);
                var totalAyer = reportes.ObtenerTotalVentasRango(ayer, ayer.AddDays(1));

                var ult7 = reportes.ObtenerVentasUltimos7Dias();
                var metodos = reportes.ObtenerVentasPorMetodoEnRango(inicioPeriodo, finPeriodo);
                if (metodos.Count == 0)
                    metodos = reportes.ObtenerVentasPorMetodoTodoElTiempo();

                var top = reportes.ObtenerProductosMasVendidos(15, inicioPeriodo);

                var carpeta = Path.Combine(Path.GetTempPath(), "GrunflexReportes");
                Directory.CreateDirectory(carpeta);

                var ruta = Path.Combine(carpeta, $"Reporte_Ventas_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
                var logoPath = LogoHelper.ObtenerRutaLogoPreferida();

                Document.Create(container =>
                {
                    container.Page(page =>
                    {
                        page.Size(PageSizes.A4);
                        page.Margin(40);
                        page.DefaultTextStyle(x => x.FontSize(10));

                        page.Header().Row(row =>
                        {
                            row.RelativeItem().Column(c =>
                            {
                                if (!string.IsNullOrWhiteSpace(logoPath) && File.Exists(logoPath))
                                    c.Item().Height(36).Image(logoPath);
                                else
                                    c.Item().Text("GRUNFLEX POS").Bold().FontSize(18).FontColor("#1E3A8A");
                                c.Item().Text("Reporte de ventas").FontSize(12).FontColor("#64748B");
                            });
                            row.ConstantItem(120).AlignRight().Text($"Generado: {DateTime.Now:dd/MM/yyyy HH:mm}");
                        });

                        page.Content().Column(col =>
                        {
                            col.Spacing(12);

                            col.Item().Background("#F1F5F9").Padding(12).Column(c =>
                            {
                                c.Item().Text("Resumen hoy").Bold().FontSize(12);
                                c.Item().Text($"Ventas del día: {totalHoy.ToString("C0", cl)} ({cantHoy} tickets)");
                                c.Item().Text($"Ticket promedio: {ticket.ToString("C0", cl)}");
                                c.Item().Text($"Ventas ayer: {totalAyer.ToString("C0", cl)}");
                                c.Item().Text($"Ventas de {titulo}: {totalPeriodo.ToString("C0", cl)} ({cantPeriodo} tickets)");
                            });

                            col.Item().Text("Últimos 7 días").Bold().FontSize(12);
                            col.Item().Table(t =>
                            {
                                t.ColumnsDefinition(cols =>
                                {
                                    cols.RelativeColumn(2);
                                    cols.RelativeColumn();
                                });

                                t.Header(h =>
                                {
                                    h.Cell().Element(CellStyle).Text("Día").Bold();
                                    h.Cell().Element(CellStyle).AlignRight().Text("Total").Bold();
                                });

                                foreach (var d in ult7)
                                {
                                    t.Cell().Element(CellStyle).Text(d.Etiqueta);
                                    t.Cell().Element(CellStyle).AlignRight().Text(d.Total.ToString("C0", cl));
                                }
                            });

                            col.Item().Text("Ventas por método de pago").Bold().FontSize(12);
                            col.Item().Table(t =>
                            {
                                t.ColumnsDefinition(cols =>
                                {
                                    cols.RelativeColumn(2);
                                    cols.RelativeColumn();
                                });

                                t.Header(h =>
                                {
                                    h.Cell().Element(CellStyle).Text("Método").Bold();
                                    h.Cell().Element(CellStyle).AlignRight().Text("Total").Bold();
                                });

                                foreach (var m in metodos.Take(20))
                                {
                                    t.Cell().Element(CellStyle).Text(m.Metodo);
                                    t.Cell().Element(CellStyle).AlignRight().Text(m.Total.ToString("C0", cl));
                                }
                            });

                            col.Item().Text($"Productos más vendidos ({titulo})").Bold().FontSize(12);
                            col.Item().Table(t =>
                            {
                                t.ColumnsDefinition(cols =>
                                {
                                    cols.RelativeColumn(3);
                                    cols.RelativeColumn();
                                    cols.RelativeColumn();
                                });

                                t.Header(h =>
                                {
                                    h.Cell().Element(CellStyle).Text("Producto").Bold();
                                    h.Cell().Element(CellStyle).AlignRight().Text("Cant.").Bold();
                                    h.Cell().Element(CellStyle).AlignRight().Text("Total").Bold();
                                });

                                foreach (var p in top)
                                {
                                    t.Cell().Element(CellStyle).Text(p.Producto);
                                    t.Cell().Element(CellStyle).AlignRight().Text(p.Cantidad.ToString());
                                    t.Cell().Element(CellStyle).AlignRight().Text(p.Total.ToString("C0", cl));
                                }
                            });
                        });

                        page.Footer().AlignCenter().Text(x =>
                        {
                            x.Span("Grunflex POS — ");
                            x.CurrentPageNumber();
                            x.Span(" / ");
                            x.TotalPages();
                        });
                    });
                })
                .GeneratePdf(ruta);

                Process.Start(new ProcessStartInfo
                {
                    FileName = ruta,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    "No se pudo generar el reporte PDF.\n" + ex.Message,
                    "Reporte",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }

        private static IContainer CellStyle(IContainer c) =>
            c.BorderBottom(1).BorderColor("#E2E8F0").PaddingVertical(4);
    }
}
