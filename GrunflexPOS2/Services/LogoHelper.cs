using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GrunflexPOS2.Services
{
    public static class LogoHelper
    {
        public const int LogoAltoPx = 63;
        public const int LogoAnchoMinPx = 300;
        public const int LogoAnchoMaxPx = 600;

        private static string ObtenerDirectorioPersistente()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GrunflexPOS",
                "Assets");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string ObtenerRutaLogoCustom()
        {
            string assets = ObtenerDirectorioPersistente();
            return Path.Combine(assets, "logo_custom.png");
        }

        public static string ObtenerRutaLogoCustomConExtension(string extension)
        {
            string ext = string.IsNullOrWhiteSpace(extension) ? ".png" : extension.Trim();
            if (!ext.StartsWith('.'))
                ext = "." + ext;

            string assets = ObtenerDirectorioPersistente();
            return Path.Combine(assets, $"logo_custom{ext.ToLowerInvariant()}");
        }

        public static string ObtenerRutaLogoCustomUnica(string extension)
        {
            string ext = string.IsNullOrWhiteSpace(extension) ? ".png" : extension.Trim();
            if (!ext.StartsWith('.'))
                ext = "." + ext;

            string assets = ObtenerDirectorioPersistente();
            return Path.Combine(assets, $"logo_custom_{DateTime.Now:yyyyMMddHHmmssfff}{ext.ToLowerInvariant()}");
        }

        public static string ObtenerRutaLogoPreferida()
        {
            try
            {
                var cfg = new ConfiguracionService();
                string custom = cfg.Get("logo_path");
                if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
                    return custom;
            }
            catch
            {
                // fallback
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidatos =
            {
                ObtenerRutaLogoCustomConExtension(".png"),
                ObtenerRutaLogoCustomConExtension(".jpg"),
                ObtenerRutaLogoCustomConExtension(".jpeg"),
                Path.Combine(baseDir, "Assets", "logo_custom.png"),
                Path.Combine(baseDir, "Assets", "logo_custom.jpg"),
                Path.Combine(baseDir, "Assets", "logo_custom.jpeg"),
                Path.Combine(baseDir, "Assets", "logo_grunflex.png"),
                Path.Combine(baseDir, "Assets", "logo.png")
            };

            foreach (var path in candidatos)
            {
                if (File.Exists(path))
                    return path;
            }

            return string.Empty;
        }

        public static BitmapImage? CargarBitmapSinCache(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return null;

                // Intento principal: cargar por bytes para evitar bloqueos de archivo.
                try
                {
                    byte[] data = File.ReadAllBytes(path);
                    if (data.Length > 0)
                    {
                        using var ms = new MemoryStream(data);
                        var bmpBytes = new BitmapImage();
                        bmpBytes.BeginInit();
                        bmpBytes.CacheOption = BitmapCacheOption.OnLoad;
                        bmpBytes.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                        bmpBytes.StreamSource = ms;
                        bmpBytes.EndInit();
                        bmpBytes.Freeze();
                        return bmpBytes;
                    }
                }
                catch
                {
                    // Continúa a fallback por URI.
                }

                // Fallback: algunos formatos/metadatos cargan mejor desde UriSource.
                var bmpUri = new BitmapImage();
                bmpUri.BeginInit();
                bmpUri.CacheOption = BitmapCacheOption.OnLoad;
                bmpUri.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmpUri.UriSource = new Uri(path, UriKind.Absolute);
                bmpUri.EndInit();
                bmpUri.Freeze();
                return bmpUri;
            }
            catch
            {
                return null;
            }
        }

        public static bool GuardarComoPngValido(string origen, string destinoPng)
        {
            try
            {
                var bmp = CargarBitmapSinCache(origen);
                if (bmp == null)
                    return false;

                var frame = BitmapFrame.Create(bmp);
                if (frame == null)
                    return false;

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(frame);
                using var fs = new FileStream(destinoPng, FileMode.Create, FileAccess.Write, FileShare.None);
                encoder.Save(fs);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool GuardarLogoNormalizado(string origen, string destinoPng)
        {
            try
            {
                var src = CargarBitmapSinCache(origen);
                if (src == null || src.PixelWidth <= 0 || src.PixelHeight <= 0)
                    return false;

                double escalaPorAlto = (double)LogoAltoPx / src.PixelHeight;
                int anchoCalculado = (int)Math.Round(src.PixelWidth * escalaPorAlto);
                int anchoFinal = Math.Max(LogoAnchoMinPx, Math.Min(LogoAnchoMaxPx, anchoCalculado));

                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, anchoFinal, LogoAltoPx));

                    double escala = Math.Min((double)anchoFinal / src.PixelWidth, (double)LogoAltoPx / src.PixelHeight);
                    double w = src.PixelWidth * escala;
                    double h = src.PixelHeight * escala;
                    double x = (anchoFinal - w) / 2d;
                    double y = (LogoAltoPx - h) / 2d;
                    dc.DrawImage(src, new Rect(x, y, w, h));
                }

                var rtb = new RenderTargetBitmap(anchoFinal, LogoAltoPx, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);
                rtb.Freeze();

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = new FileStream(destinoPng, FileMode.Create, FileAccess.Write, FileShare.None);
                encoder.Save(fs);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void LimpiarLogosAntiguos()
        {
            try
            {
                string assets = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
                if (Directory.Exists(assets))
                {
                    foreach (var f in Directory.GetFiles(assets, "logo_custom_*.*"))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }

                string persist = ObtenerDirectorioPersistente();
                if (Directory.Exists(persist))
                {
                    foreach (var f in Directory.GetFiles(persist, "logo_custom_*.*"))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch
            {
                // noop
            }
        }

        public static void LimpiarLogosCustomExcept(string keepPath)
        {
            try
            {
                string keep = (keepPath ?? string.Empty).Trim();
                string[] roots =
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets"),
                    ObtenerDirectorioPersistente()
                };

                foreach (var root in roots)
                {
                    if (!Directory.Exists(root))
                        continue;
                    foreach (var f in Directory.GetFiles(root, "logo_custom*.*"))
                    {
                        if (string.Equals(f, keep, StringComparison.OrdinalIgnoreCase))
                            continue;
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch
            {
                // noop
            }
        }

        public static void CopiarArchivoRobusto(string origen, string destino)
        {
            byte[] data = File.ReadAllBytes(origen);
            Exception? last = null;
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    File.WriteAllBytes(destino, data);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    System.Threading.Thread.Sleep(80 * (i + 1));
                }
            }

            throw new IOException($"No se pudo copiar archivo a '{destino}'.", last);
        }
    }
}
