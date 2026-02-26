using System.Windows;
using PdfSharp.Fonts;
using GrunflexPOS2.Services;

namespace GrunflexPOS2
{
    public partial class App : Application
    {
        public App()
        {
            // 🔥 REGISTRAR FUENTE PERSONALIZADA PARA PDFSHARP
            GlobalFontSettings.FontResolver = new CustomFontResolver();
        }
    }
}
