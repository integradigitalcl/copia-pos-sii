using System.Windows;
using PdfSharp.Fonts;
using GrunflexPOS2.Services;

namespace GrunflexPOS2
{
    public partial class App : Application
    {
        // 🔥 INSTANCIA GLOBAL CONTROLADA DE VentaService
        public static VentaService VentaService { get; private set; } = null!;

        public App()
        {
            // 🔥 REGISTRAR FUENTE PERSONALIZADA PARA PDFSHARP
            GlobalFontSettings.FontResolver = new CustomFontResolver();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 🔥 INICIALIZAR SERVICIO DE VENTAS UNA SOLA VEZ
            VentaService = new VentaService();
        }
    }
}