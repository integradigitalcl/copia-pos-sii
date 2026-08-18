using System;
using System.IO;
using GrunflexPOS2.Models;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using QuestPDF.Helpers;
using QRCoder;

namespace GrunflexPOS2.Services
{
    public class BoletaPdfService
    {
        private const int ANCHO_MM = 72;

        public string GenerarBoletaPdf(Venta venta)
        {
            string carpeta = Path.Combine(Path.GetTempPath(), "GrunflexBoletas");

            if (!Directory.Exists(carpeta))
                Directory.CreateDirectory(carpeta);

            string rutaArchivo = Path.Combine(
                carpeta,
                $"Boleta_{venta.NumeroTicket}_{DateTime.Now:yyyyMMddHHmmss}.pdf"
            );

            float ancho = MmToPoints(ANCHO_MM);

            var cfg = new ConfiguracionService();
            string logoPath = cfg.Get("boleta_logo_path");
            if (string.IsNullOrWhiteSpace(logoPath) || !File.Exists(logoPath))
                logoPath = LogoHelper.ObtenerRutaLogoPreferida();

            // 🔥 GENERAR QR
            byte[] qrBytes = GenerarQr(venta);

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(ancho, 1600);
                    page.Margin(3);
                    page.DefaultTextStyle(x => x.FontSize(7));

                    page.Content().Column(col =>
                    {
                        // LOGO
                        if (File.Exists(logoPath))
                            col.Item().AlignCenter().Height(50).Image(logoPath);

                        // BLOQUE SII
                        col.Item().Border(1).Padding(4).Column(box =>
                        {
                            box.Item().AlignCenter().Text("R.U.T. 78.080.108-5").Bold();
                            box.Item().AlignCenter().Text("BOLETA ELECTRÓNICA").Bold();
                            box.Item().AlignCenter().Text($"N° {venta.NumeroTicket}")
                                .FontSize(10).Bold();
                        });

                        col.Item().AlignCenter().Text("SII - Valparaíso").FontSize(6);

                        col.Item().PaddingVertical(3);

                        // EMPRESA
                        col.Item().AlignCenter().Text("GRUNFLEX COMPUTACIÓN SPA").Bold();
                        col.Item().Text("Giro: Venta de Equipos Computacionales").FontSize(6);
                        col.Item().Text("Dirección: Santiago, Chile").FontSize(6);

                        col.Item().Text($"Fecha: {venta.Fecha:dd/MM/yyyy HH:mm}");

                        col.Item().LineHorizontal(1);

                        // TABLA
                        foreach (var item in venta.Items)
                        {
                            col.Item().Row(row =>
                            {
                                row.RelativeItem().Text(item.Producto);
                                row.ConstantItem(30).AlignRight().Text(item.Cantidad.ToString());
                                row.ConstantItem(40).AlignRight().Text($"${item.Importe}");
                            });
                        }

                        col.Item().LineHorizontal(1);

                        decimal iva = venta.Total * 0.19m;

                        col.Item().Row(row =>
                        {
                            row.RelativeItem().Text("IVA:");
                            row.ConstantItem(40).AlignRight().Text($"${iva:0}");
                        });

                        col.Item().Row(row =>
                        {
                            row.RelativeItem().Text("TOTAL").Bold();
                            row.ConstantItem(40).AlignRight()
                                .Text($"${venta.Total}")
                                .Bold();
                        });

                        col.Item().PaddingVertical(5);

                        // 🔥 QR REAL
                        col.Item().AlignCenter().Height(80).Image(qrBytes);

                        col.Item().AlignCenter().Text("Verifique su boleta").FontSize(6);

                        col.Item().AlignCenter().Text("Gracias por su compra").FontSize(6);
                    });
                });
            })
            .GeneratePdf(rutaArchivo);

            return rutaArchivo;
        }

        private byte[] GenerarQr(Venta venta)
        {
            string contenido = $"Ticket:{venta.NumeroTicket}|Total:{venta.Total}|Fecha:{venta.Fecha}";

            using (var qrGenerator = new QRCodeGenerator())
            {
                var data = qrGenerator.CreateQrCode(contenido, QRCodeGenerator.ECCLevel.Q);
                var qrCode = new PngByteQRCode(data);
                return qrCode.GetGraphic(5);
            }
        }

        private float MmToPoints(float mm)
        {
            return mm * 2.83465f;
        }
    }
}