using PdfSharp.Fonts;
using System;
using System.IO;

namespace GrunflexPOS2.Services
{
    public class CustomFontResolver : IFontResolver
    {
        public byte[] GetFont(string faceName)
        {
            var fontPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Fonts",
                "arial.ttf"
            );

            if (!File.Exists(fontPath))
                throw new FileNotFoundException("No se encontró la fuente en: " + fontPath);

            return File.ReadAllBytes(fontPath);
        }

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            return new FontResolverInfo("Arial#");
        }
    }
}
