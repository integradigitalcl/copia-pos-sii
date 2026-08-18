using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Views
{
    public partial class CajonDineroView : UserControl
    {
        private readonly ConfiguracionService _cfg = new();
        private readonly List<string> _opciones = new()
        {
            "- Ninguno -",
            "Apertura a través de impresión",
            "Citizen",
            "Epson Cajon 1",
            "Epson Cajon 2",
            "Epson Metodo 2",
            "Epson TM-T88III",
            "Epson TM-U220",
            "Ithaca",
            "PostLine",
            "Star 1",
            "Star 2",
            "Star SP500",
            "Star TSP600",
            "Star TSP600 II",
            "LPT1",
            "LPT2",
            "LPT3",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "USB"
        };

        public CajonDineroView()
        {
            InitializeComponent();
            CargarOpciones();
        }

        private void CargarOpciones()
        {
            var opciones = new List<string>(_opciones);
            try
            {
                var impresoras = System.Drawing.Printing.PrinterSettings.InstalledPrinters.Cast<string>()
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();
                foreach (var imp in impresoras)
                {
                    if (!opciones.Contains(imp))
                        opciones.Add(imp);
                }
            }
            catch
            {
                // noop
            }

            cmbPuerto.ItemsSource = opciones;
            string actual = _cfg.Get("cajon_modelo");
            if (!string.IsNullOrWhiteSpace(actual) && opciones.Contains(actual))
                cmbPuerto.SelectedItem = actual;
            else
                cmbPuerto.SelectedIndex = 0;
            TxtEstado.Text = $"Modelo actual: {cmbPuerto.SelectedItem}";
            BadgeResultado.Background = new SolidColorBrush(Color.FromRgb(107, 114, 128));
            TxtResultado.Text = "Último intento: pendiente";
        }

        private void CmbPuerto_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string modelo = cmbPuerto.SelectedItem?.ToString() ?? "- Ninguno -";
            _cfg.Set("cajon_modelo", modelo);
            TxtEstado.Text = $"Modelo actual: {modelo}";
            BadgeResultado.Background = new SolidColorBrush(Color.FromRgb(59, 130, 246));
            TxtResultado.Text = "Configuración guardada";
        }

        private void BtnProbar_Click(object sender, RoutedEventArgs e)
        {
            string modelo = cmbPuerto.SelectedItem?.ToString() ?? "- Ninguno -";
            _cfg.Set("cajon_modelo", modelo);

            if (!CajonDineroService.ProbarApertura(modelo, out string error))
            {
                BadgeResultado.Background = new SolidColorBrush(Color.FromRgb(153, 27, 27));
                TxtResultado.Text = $"Último intento: error ({DateTime.Now:HH:mm:ss})";
                MessageBox.Show("No se pudo abrir cajón:\n" + error);
                return;
            }

            BadgeResultado.Background = new SolidColorBrush(Color.FromRgb(22, 163, 74));
            TxtResultado.Text = $"Último intento: OK ({DateTime.Now:HH:mm:ss})";
            MessageBox.Show("Comando de apertura enviado correctamente.");
        }
    }
}