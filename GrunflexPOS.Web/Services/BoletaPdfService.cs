using System.Diagnostics;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QRCoder;

namespace GrunflexPOS.Web.Services;

public sealed class BoletaPdfService(PosLogoService posLogo, ILogger<BoletaPdfService> logger)
{
    public string Generate(long ticketNumber, IReadOnlyCollection<CartItem> items, decimal total,
        string customer = "Público en general", bool openInExplorer = true)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GrunflexPOS", "Boletas");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"Boleta_{ticketNumber}_{DateTime.Now:yyyyMMddHHmmss}.pdf");
        var qr = GenerateQr(ticketNumber, total);
        var logoBytes = posLogo.GetLogoBytesAsync().GetAwaiter().GetResult();

        Document.Create(container => container.Page(page =>
        {
            page.Size(MmToPoints(72), 1600);
            page.Margin(3);
            page.DefaultTextStyle(x => x.FontSize(7));
            page.Content().Column(column =>
            {
                if (logoBytes is { Length: > 0 })
                    column.Item().AlignCenter().Height(50).Image(logoBytes);
                column.Item().Border(1).Padding(4).Column(box =>
                {
                    box.Item().AlignCenter().Text("R.U.T. 78.080.108-5").Bold();
                    box.Item().AlignCenter().Text("BOLETA ELECTRÓNICA").Bold();
                    box.Item().AlignCenter().Text($"N° {ticketNumber}").FontSize(10).Bold();
                });
                column.Item().AlignCenter().Text("SII - Valparaíso").FontSize(6);
                column.Item().PaddingVertical(3);
                column.Item().AlignCenter().Text("GRUNFLEX COMPUTACIÓN SPA").Bold();
                column.Item().Text("Giro: Venta de Equipos Computacionales").FontSize(6);
                column.Item().Text("Dirección: Santiago, Chile").FontSize(6);
                column.Item().Text($"Fecha: {DateTime.Now:dd/MM/yyyy HH:mm}");
                column.Item().Text($"Cliente: {customer}");
                column.Item().LineHorizontal(1);

                foreach (var item in items)
                {
                    column.Item().Row(row =>
                    {
                        row.RelativeItem().Text(item.Product.Name);
                        row.ConstantItem(30).AlignRight().Text(item.Quantity.ToString("0.##"));
                        row.ConstantItem(45).AlignRight().Text($"${item.Product.Price * item.Quantity:0}");
                    });
                }

                column.Item().LineHorizontal(1);
                column.Item().Row(row =>
                {
                    row.RelativeItem().Text("IVA:");
                    row.ConstantItem(45).AlignRight().Text($"${total * .19m:0}");
                });
                column.Item().Row(row =>
                {
                    row.RelativeItem().Text("TOTAL").Bold();
                    row.ConstantItem(45).AlignRight().Text($"${total:0}").Bold();
                });
                column.Item().PaddingVertical(5);
                column.Item().AlignCenter().Height(80).Image(qr);
                column.Item().AlignCenter().Text("Verifique su boleta").FontSize(6);
                column.Item().AlignCenter().Text("Gracias por su compra").FontSize(6);
            });
        })).GeneratePdf(path);

        try
        {
            if (openInExplorer)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No se pudo abrir la boleta generada");
        }
        return path;
    }

    private static byte[] GenerateQr(long ticketNumber, decimal total)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(
            $"Ticket:{ticketNumber}|Total:{total:0}|Fecha:{DateTime.Now:O}",
            QRCodeGenerator.ECCLevel.Q);
        return new PngByteQRCode(data).GetGraphic(5);
    }

    private static float MmToPoints(float millimeters) => millimeters * 2.83465f;
}
