using PdfSharp.Fonts;
using System;
using System.IO;

namespace GrunflexPOS2.Services
{
    public class CustomFontResolver : IFontResolver
    {
        private static readonly Lazy<CustomFontResolver> _instance = new(() => new CustomFontResolver());
        public static CustomFontResolver Instance => _instance.Value;

        private const string RegularFace = "Grunflex#Regular";
        private const string BoldFace = "Grunflex#Bold";

        public byte[] GetFont(string faceName)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;

            var regularPath = Path.Combine(baseDir, "Fonts", "ARIAL.TTF");
            var boldPath = Path.Combine(baseDir, "Fonts", "ARIALBD.TTF");

            if (faceName == BoldFace && File.Exists(boldPath))
                return File.ReadAllBytes(boldPath);

            if (File.Exists(regularPath))
                return File.ReadAllBytes(regularPath);

            throw new FileNotFoundException(
                $"No se encontró fuente embebida para PDF. Buscado: {regularPath} y {boldPath}");
        }

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            // Para tickets no usamos cursiva. Cualquier familia solicitada se mapea a Arial embebida.
            return new FontResolverInfo(isBold ? BoldFace : RegularFace);
        }
    }
}
