using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GrunflexPOS.Web.Services;

public sealed class ReportsPdfService
{
    public byte[] Generate(ReportDashboard report, DateTime fromLocal, DateTime toLocal)
    {
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(32);
            page.DefaultTextStyle(style => style.FontSize(9));
            page.Header().Column(column =>
            {
                column.Item().Text("GRUNFLEX POS").FontSize(20).Bold().FontColor("#183B56");
                column.Item().Text("Reportes de ventas").FontSize(12).SemiBold();
                column.Item().Text($"Periodo: {fromLocal:dd/MM/yyyy HH:mm} al {toLocal:dd/MM/yyyy HH:mm}")
                    .FontColor("#52606D");
            });
            page.Content().PaddingTop(18).Column(column =>
            {
                column.Spacing(10);
                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                    });
                    AddMetric(table, "Ventas totales", report.Total.ToString("C0"), "#2563EB");
                    AddMetric(table, "Ganancia", report.Profit.ToString("C0"), "#0D9488");
                    AddMetric(table, "Nº ventas", report.Transactions.ToString(), "#7C3AED");
                    AddMetric(table, "Margen", $"{report.AvgMargin:0.##}%", "#EA580C");
                });

                AddHeading(column, "Ventas por día");
                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(2);
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                    });
                    AddHeader(table, "Día", "Ventas", "Ganancia");
                    foreach (var day in report.DaySeries)
                    {
                        table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(5).Text(day.Label);
                        table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(5).AlignRight()
                            .Text(day.Sales.ToString("C0"));
                        table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(5).AlignRight()
                            .Text(day.Profit.ToString("C0"));
                    }
                });

                AddHeading(column, "Ventas por departamento");
                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(2);
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                    });
                    table.Cell().Background("#EAF1F7").Padding(5).Text("Departamento").Bold();
                    table.Cell().Background("#EAF1F7").Padding(5).AlignRight().Text("Ventas").Bold();
                    table.Cell().Background("#EAF1F7").Padding(5).AlignRight().Text("Ganancia").Bold();
                    table.Cell().Background("#EAF1F7").Padding(5).AlignRight().Text("%").Bold();
                    foreach (var dept in report.Departments)
                    {
                        table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(5).Text(dept.Department);
                        table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(5).AlignRight()
                            .Text(dept.Revenue.ToString("C0"));
                        table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(5).AlignRight()
                            .Text(dept.Profit.ToString("C0"));
                        table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(5).AlignRight()
                            .Text($"{dept.SharePercent:0.0}%");
                    }
                });

                AddHeading(column, "Métodos de pago");
                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(2);
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                    });
                    AddHeader(table, "Método", "Operaciones", "Total");
                    foreach (var payment in report.Payments)
                    {
                        table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(5).Text(payment.Method);
                        table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(5).Text(payment.Transactions.ToString());
                        table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(5).AlignRight()
                            .Text(payment.Total.ToString("C0"));
                    }
                });

                AddHeading(column, "Impuestos");
                column.Item().Text(
                    $"IVA ({report.TaxRate:0.####}%) · Cobrado: {report.TaxCollected:C0} · Ventas gravadas: {report.TaxableSales:C0}");
            });
            page.Footer().AlignCenter().Text(text =>
            {
                text.Span("Generado localmente · ");
                text.CurrentPageNumber();
            });
        })).GeneratePdf();
    }

    private static void AddMetric(TableDescriptor table, string title, string value, string color)
    {
        table.Cell().Background(color).Padding(9).Column(column =>
        {
            column.Item().Text(title).FontColor(Colors.White).FontSize(8);
            column.Item().Text(value).FontColor(Colors.White).FontSize(14).Bold();
        });
    }

    private static void AddHeading(ColumnDescriptor column, string title) =>
        column.Item().PaddingTop(8).Text(title).FontSize(12).Bold().FontColor("#183B56");

    private static void AddHeader(TableDescriptor table, string first, string second, string third)
    {
        table.Cell().Background("#EAF1F7").Padding(5).Text(first).Bold();
        table.Cell().Background("#EAF1F7").Padding(5).Text(second).Bold();
        table.Cell().Background("#EAF1F7").Padding(5).AlignRight().Text(third).Bold();
    }
}
