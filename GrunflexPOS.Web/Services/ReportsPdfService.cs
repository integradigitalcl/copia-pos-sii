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
                column.Item().Text("Reporte de operación").FontSize(12).SemiBold();
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
                    AddMetric(table, "Ventas", report.Total.ToString("C0"), "#2563EB");
                    AddMetric(table, "Transacciones", report.Transactions.ToString(), "#16A34A");
                    AddMetric(table, "Ticket promedio", report.AverageTicket.ToString("C0"), "#7C3AED");
                    AddMetric(table, "Variación", $"{report.Variation:0.##}%", "#EA580C");
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
                AddHeading(column, "Productos más vendidos");
                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(2);
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                    });
                    AddHeader(table, "Producto", "Unidades", "Ingresos");
                    foreach (var product in report.TopProducts)
                    {
                        table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(5)
                            .Text($"{product.Name} ({product.Code})");
                        table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(5)
                            .Text(product.Quantity.ToString("0.##"));
                        table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(5).AlignRight()
                            .Text(product.Revenue.ToString("C0"));
                    }
                });
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
