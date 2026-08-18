using PdfSharp.Pdf;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using System;
using System.Diagnostics;
using System.IO;
using GrunflexPOS2.Models;

namespace GrunflexPOS2.Services
{
    public static class TicketPdfService
    {
        private static bool _fontResolverReady;

        private static void EnsureFontResolver()
        {
            if (_fontResolverReady)
                return;

            if (GlobalFontSettings.FontResolver == null)
                GlobalFontSettings.FontResolver = CustomFontResolver.Instance;

            _fontResolverReady = true;
        }

        public static bool TryGenerarTicketPDF(Venta venta, out string rutaGenerada, out string? error)
        {
            rutaGenerada = string.Empty;
            error = null;
            try
            {
                EnsureFontResolver();

                var cfg = new ConfiguracionService();
                var fuenteNombre = cfg.Get("ticket_fuente");
                if (string.IsNullOrWhiteSpace(fuenteNombre))
                    fuenteNombre = "Courier New";

                int tamFuente = 9;
                if (!int.TryParse(cfg.Get("ticket_tamano"), out tamFuente) || tamFuente < 6)
                    tamFuente = 9;

                int columnas = 36;
                if (!int.TryParse(cfg.Get("ticket_columnas"), out columnas) || columnas < 20)
                    columnas = 36;

                int anchoMm = 80;
                if (!int.TryParse(cfg.Get("ticket_ancho_mm"), out anchoMm) || (anchoMm != 58 && anchoMm != 80))
                    anchoMm = 80;

                bool usarTotalesNormal = cfg.Get("ticket_totales_normal") == "true";
                bool negrita = cfg.Get("ticket_negrita") == "true";
                bool incluirPrecioUnitario = cfg.Get("ticket_incluir_precio_unitario") == "true";
                bool descripcionExtendida = cfg.Get("ticket_descripcion_extendida") == "true";
                bool datosCliente = cfg.Get("ticket_datos_cliente") == "true";
                int lineasArriba = 0;
                int.TryParse(cfg.Get("ticket_lineas_arriba"), out lineasArriba);
                if (lineasArriba < 0) lineasArriba = 0;
                int lineasAbajo = 0;
                int.TryParse(cfg.Get("ticket_lineas_abajo"), out lineasAbajo);
                if (lineasAbajo < 0) lineasAbajo = 0;
                string linea1 = Valor(cfg, "ticket_linea_1", "GRUNFLEX POS");
                string linea2 = Valor(cfg, "ticket_linea_2", "");
                string linea3 = Valor(cfg, "ticket_linea_3", "");
                string linea4 = Valor(cfg, "ticket_linea_4", "");
                string linea5 = Valor(cfg, "ticket_linea_5", "Cant.  Descripcion               Importe");
                string linea6 = Valor(cfg, "ticket_linea_6", "----------------------------------------");
                string linea10 = Valor(cfg, "ticket_linea_10", "");
                string linea11 = Valor(cfg, "ticket_linea_11", "");
                string linea12 = Valor(cfg, "ticket_linea_12", "Gracias por su compra");
                string linea13 = Valor(cfg, "ticket_linea_13", "");
                var totalActualTexto = venta.Total.ToString("C");
                var articulosActualTexto = venta.TotalArticulos.ToString();

                var documento = new PdfDocument();
                documento.Info.Title = $"Ticket_{venta.NumeroTicket}";

                var pagina = documento.AddPage();
                pagina.Width = XUnit.FromMillimeter(anchoMm);
                pagina.Height = XUnit.FromMillimeter(200);

                var gfx = XGraphics.FromPdfPage(pagina);

                var estiloBase = negrita ? XFontStyleEx.Bold : XFontStyleEx.Regular;
                var fuenteTitulo = new XFont(fuenteNombre, tamFuente + 2, XFontStyleEx.Bold);
                var fuenteNormal = new XFont(fuenteNombre, tamFuente, estiloBase);
                var fuenteTotal = usarTotalesNormal ? fuenteNormal : new XFont(fuenteNombre, tamFuente + 1, XFontStyleEx.Bold);
                var areaContenido = new XRect(10, 0, pagina.Width.Point - 20, pagina.Height.Point);

                double y = 10;

                y += lineasArriba * (tamFuente + 1);

                // ================= LOGO =================
                string logoPath = LogoHelper.ObtenerRutaLogoPreferida();

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
                gfx.DrawString(Recortar(linea1, columnas), fuenteTitulo, XBrushes.Black,
                    new XRect(areaContenido.Left, y, areaContenido.Width, 20),
                    XStringFormats.TopLeft);

                y += 20;

                if (!string.IsNullOrWhiteSpace(linea2))
                {
                    gfx.DrawString(Recortar(linea2, columnas), fuenteNormal, XBrushes.Black,
                        new XRect(areaContenido.Left, y, areaContenido.Width, 15),
                        XStringFormats.TopLeft);
                    y += 15;
                }

                if (!string.IsNullOrWhiteSpace(linea3))
                {
                    gfx.DrawString(Recortar(linea3, columnas), fuenteNormal, XBrushes.Black,
                        new XRect(areaContenido.Left, y, areaContenido.Width, 15),
                        XStringFormats.TopLeft);
                    y += 15;
                }

                if (!string.IsNullOrWhiteSpace(linea4))
                {
                    gfx.DrawString(Recortar(linea4, columnas), fuenteNormal, XBrushes.Black,
                        new XRect(areaContenido.Left, y, areaContenido.Width, 15),
                        XStringFormats.TopLeft);
                    y += 15;
                }

                gfx.DrawString($"Ticket N° {venta.NumeroTicket}", fuenteNormal, XBrushes.Black,
                    new XRect(areaContenido.Left, y, areaContenido.Width, 15),
                    XStringFormats.TopLeft);

                y += 15;

                gfx.DrawString($"Fecha: {venta.Fecha:dd/MM/yyyy HH:mm}", fuenteNormal, XBrushes.Black,
                    new XRect(areaContenido.Left, y, areaContenido.Width, 15),
                    XStringFormats.TopLeft);

                y += 20;

                if (!string.IsNullOrWhiteSpace(linea5))
                {
                    gfx.DrawString(Recortar(linea5, columnas), fuenteNormal, XBrushes.Black,
                        new XRect(areaContenido.Left, y, areaContenido.Width, 15), XStringFormats.TopLeft);
                    y += 12;
                }
                if (!string.IsNullOrWhiteSpace(linea6))
                {
                    gfx.DrawString(Recortar(linea6, columnas), fuenteNormal, XBrushes.Black,
                        new XRect(areaContenido.Left, y, areaContenido.Width, 15), XStringFormats.TopLeft);
                    y += 12;
                }

                gfx.DrawLine(XPens.Black, 10, y, pagina.Width - 10, y);
                y += 10;

                // ================= ITEMS =================
                foreach (var item in venta.Items)
                {
                    var nombreProducto = descripcionExtendida ? item.Producto : Recortar(item.Producto, Math.Max(12, columnas - 16));

                    var lineaPrincipal = incluirPrecioUnitario
                        ? $"{item.Cantidad} x {item.Precio:C} {nombreProducto}"
                        : $"{item.Cantidad} x {nombreProducto}";

                    gfx.DrawString(
                        Recortar(lineaPrincipal, columnas),
                        fuenteNormal,
                        XBrushes.Black,
                        new XRect(areaContenido.Left, y, areaContenido.Width, 15),
                        XStringFormats.TopLeft);

                    y += 12;

                    gfx.DrawString(
                        $"{item.Importe:C}",
                        fuenteNormal,
                        XBrushes.Black,
                        new XRect(areaContenido.Left, y, areaContenido.Width, 15),
                        XStringFormats.TopRight);

                    y += 18;
                }

                gfx.DrawLine(XPens.Black, 10, y, pagina.Width - 10, y);
                y += 15;

                // ================= TOTAL =================
                gfx.DrawString(
                    "TOTAL:",
                    fuenteTotal,
                    XBrushes.Black,
                    new XRect(areaContenido.Left, y, areaContenido.Width, 20),
                    XStringFormats.TopLeft);
                gfx.DrawString(
                    $"{venta.Total:C}",
                    fuenteTotal,
                    XBrushes.Black,
                    new XRect(areaContenido.Left, y, areaContenido.Width, 20),
                    XStringFormats.TopRight);

                y += 25;

                if (!string.IsNullOrWhiteSpace(linea10))
                {
                    var linea10Render = RenderLineaDinamica(linea10, totalActualTexto, articulosActualTexto);
                    gfx.DrawString(Recortar(linea10Render, columnas), fuenteNormal, XBrushes.Black,
                        new XRect(areaContenido.Left, y, areaContenido.Width, 15), XStringFormats.TopLeft);
                    y += 15;
                }

                if (!string.IsNullOrWhiteSpace(linea11))
                {
                    var linea11Render = RenderLineaDinamica(linea11, totalActualTexto, articulosActualTexto);
                    gfx.DrawString(Recortar(linea11Render, columnas), fuenteTotal, XBrushes.Black,
                        new XRect(areaContenido.Left, y, areaContenido.Width, 15), XStringFormats.TopLeft);
                    y += 15;
                }

                if (datosCliente && !string.IsNullOrWhiteSpace(venta.Cliente))
                {
                    gfx.DrawString(
                        $"Cliente: {Recortar(venta.Cliente, columnas)}",
                        fuenteNormal,
                        XBrushes.Black,
                        new XRect(areaContenido.Left, y, areaContenido.Width, 15),
                        XStringFormats.TopLeft);
                    y += 15;
                }

                gfx.DrawString(
                    Recortar(linea12, columnas),
                    fuenteNormal,
                    XBrushes.Black,
                    new XRect(areaContenido.Left, y, areaContenido.Width, 15),
                    XStringFormats.TopLeft);
                y += 15;

                if (!string.IsNullOrWhiteSpace(linea13))
                {
                    gfx.DrawString(
                        Recortar(linea13, columnas),
                        fuenteNormal,
                        XBrushes.Black,
                        new XRect(areaContenido.Left, y, areaContenido.Width, 15),
                        XStringFormats.TopLeft);
                    y += 15;
                }
                y += lineasAbajo * (tamFuente + 1);

                // ================= GUARDAR =================
                var carpeta = ObtenerCarpetaTickets();
                Directory.CreateDirectory(carpeta);

                string ruta = Path.Combine(
                    carpeta,
                    $"Ticket_{venta.NumeroTicket}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

                documento.Save(ruta);

                AbrirPdfDesacoplado(ruta);
                rutaGenerada = ruta;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static void GenerarTicketPDF(Venta venta)
        {
            if (!TryGenerarTicketPDF(venta, out _, out var error))
            {
                System.Windows.MessageBox.Show("Error al generar ticket: " + error);
            }
        }

        private static string Recortar(string texto, int max)
        {
            if (string.IsNullOrWhiteSpace(texto))
                return string.Empty;
            return texto.Length <= max ? texto : texto.Substring(0, max);
        }

        private static string Valor(ConfiguracionService cfg, string key, string def)
        {
            var v = cfg.Get(key);
            return string.IsNullOrWhiteSpace(v) ? def : v;
        }

        private static string ObtenerCarpetaTickets()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(localAppData, "GrunflexPOS", "Tickets");
            }

            var misDocumentos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(misDocumentos, "GrunflexTickets");
        }

        private static string RenderLineaDinamica(string linea, string totalActual, string totalArticulos)
        {
            if (string.IsNullOrWhiteSpace(linea))
                return string.Empty;

            var texto = linea
                .Replace("{TOTAL}", totalActual, StringComparison.OrdinalIgnoreCase)
                .Replace("{ARTICULOS}", totalArticulos, StringComparison.OrdinalIgnoreCase);

            // Evita desajustes cuando la configuración trae un total fijo antiguo.
            if (texto.StartsWith("total:", StringComparison.OrdinalIgnoreCase))
                return $"Total: {totalActual}";

            if (texto.StartsWith("no. de articulos:", StringComparison.OrdinalIgnoreCase) ||
                texto.StartsWith("no de articulos:", StringComparison.OrdinalIgnoreCase))
                return $"No. de Articulos: {totalArticulos}";

            return texto;
        }

        private static void AbrirPdfDesacoplado(string rutaPdf)
        {
            // Abrir con explorer.exe desacopla completamente del proceso del POS.
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{rutaPdf}\"",
                UseShellExecute = true
            });
        }
    }
}
