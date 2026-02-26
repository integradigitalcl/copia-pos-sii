using PdfSharp.Pdf;
using PdfSharp.Drawing;
using System;
using System.Diagnostics;
using System.IO;
using GrunflexPOS2.Models;

namespace GrunflexPOS2.Services
{
    public static class TicketPdfService
    {
        public static void GenerarTicketPDF(Venta venta)
        {
            try
            {
                var documento = new PdfDocument();
                documento.Info.Title = $"Ticket_{venta.NumeroTicket}";

                var pagina = documento.AddPage();
                pagina.Width = XUnit.FromMillimeter(80); // tamaño ticket térmico
                pagina.Height = XUnit.FromMillimeter(200);

                var gfx = XGraphics.FromPdfPage(pagina);

                var fuenteTitulo = new XFont("Courier New", 12, XFontStyleEx.Bold);
                var fuenteNormal = new XFont("Courier New", 9);

                double y = 10;

                // ================= LOGO =================
                string logoPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Assets",
                    "logo_grunflex.png");

                if (File.Exists(logoPath))
                {
                    var logo = XImage.FromFile(logoPath);

                    double logoWidth = 120;
                    double logoHeight = logo.PixelHeight * logoWidth / logo.PixelWidth;

                    gfx.DrawImage(logo,
                        (pagina.Width - logoWidth) / 2,
                        y,
                        logoWidth,
                        logoHeight);

                    y += logoHeight + 10;
                }

                // ================= EMPRESA =================
                gfx.DrawString("GRUNFLEX", fuenteTitulo, XBrushes.Black,
                    new XRect(0, y, pagina.Width, 20),
                    XStringFormats.TopCenter);

                y += 20;

                gfx.DrawString($"Ticket N° {venta.NumeroTicket}", fuenteNormal, XBrushes.Black,
                    new XRect(0, y, pagina.Width, 15),
                    XStringFormats.TopCenter);

                y += 15;

                gfx.DrawString($"Fecha: {venta.Fecha:dd/MM/yyyy HH:mm}", fuenteNormal, XBrushes.Black,
                    new XRect(0, y, pagina.Width, 15),
                    XStringFormats.TopCenter);

                y += 20;

                gfx.DrawLine(XPens.Black, 10, y, pagina.Width - 10, y);
                y += 10;

                // ================= ITEMS =================
                foreach (var item in venta.Items)
                {
                    gfx.DrawString(
                        $"{item.Cantidad} x {item.Producto}",
                        fuenteNormal,
                        XBrushes.Black,
                        new XRect(10, y, pagina.Width - 20, 15),
                        XStringFormats.TopLeft);

                    y += 12;

                    gfx.DrawString(
                        $"{item.Importe:C}",
                        fuenteNormal,
                        XBrushes.Black,
                        new XRect(10, y, pagina.Width - 20, 15),
                        XStringFormats.TopRight);

                    y += 18;
                }

                gfx.DrawLine(XPens.Black, 10, y, pagina.Width - 10, y);
                y += 15;

                // ================= TOTAL =================
                gfx.DrawString(
                    $"TOTAL: {venta.Total:C}",
                    fuenteTitulo,
                    XBrushes.Black,
                    new XRect(0, y, pagina.Width, 20),
                    XStringFormats.TopCenter);

                y += 25;

                gfx.DrawString(
                    "Gracias por su compra",
                    fuenteNormal,
                    XBrushes.Black,
                    new XRect(0, y, pagina.Width, 15),
                    XStringFormats.TopCenter);

                // ================= GUARDAR =================
                string carpeta = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "GrunflexTickets");

                if (!Directory.Exists(carpeta))
                    Directory.CreateDirectory(carpeta);

                string ruta = Path.Combine(
                    carpeta,
                    $"Ticket_{venta.NumeroTicket}.pdf");

                documento.Save(ruta);

                // Abrir automáticamente
                Process.Start(new ProcessStartInfo
                {
                    FileName = ruta,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    "Error al generar ticket: " + ex.Message);
            }
        }
    }
}
