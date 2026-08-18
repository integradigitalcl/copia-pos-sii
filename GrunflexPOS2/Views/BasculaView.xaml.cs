using System;
using System.IO.Ports;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Views
{
    public partial class BasculaView : UserControl
    {
        private readonly ConfiguracionService _cfg = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
        private bool _cargando;

        public BasculaView()
        {
            _cargando = true;
            InitializeComponent();

            Loaded += BasculaView_Loaded;
            Unloaded += BasculaView_Unloaded;
            _timer.Tick += (_, _) => RefrescarDeteccionDrivers();
        }

        private void BasculaView_Loaded(object sender, RoutedEventArgs e)
        {
            _cargando = true;
            ChkBasculaActiva.IsChecked = _cfg.Get("bascula_activa") == "true";
            TxtCodigoInicial.Text = string.IsNullOrWhiteSpace(_cfg.Get("bascula_codigo_inicial")) ? "2000" : _cfg.Get("bascula_codigo_inicial");
            ChkEtiquetaPrecio.IsChecked = _cfg.Get("bascula_etiqueta_precio") == "true";
            ChkEtiquetaPeso.IsChecked = _cfg.Get("bascula_etiqueta_peso") == "true";

            RefrescarDeteccionDrivers();
            string guardado = _cfg.Get("bascula_driver_puerto");
            if (!string.IsNullOrWhiteSpace(guardado))
            {
                var item = CmbDriverBascula.Items.Cast<string>()
                    .FirstOrDefault(x => string.Equals(x, guardado, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                    CmbDriverBascula.SelectedItem = item;
            }
            if (CmbDriverBascula.SelectedItem == null && CmbDriverBascula.Items.Count > 0)
                CmbDriverBascula.SelectedIndex = 0;
            _cargando = false;
            _timer.Start();
        }

        private void BasculaView_Unloaded(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
        }

        private void RefrescarDeteccionDrivers()
        {
            var puertos = SerialPort.GetPortNames().OrderBy(x => x).ToList();
            var lista = puertos.Any()
                ? puertos.Select(p => $"Serial {p} (driver OK)").ToList()
                : new System.Collections.Generic.List<string> { "Sin dispositivos seriales detectados" };

            string? sel = CmbDriverBascula.SelectedItem?.ToString();
            CmbDriverBascula.ItemsSource = lista;
            if (!string.IsNullOrWhiteSpace(sel) && lista.Contains(sel))
                CmbDriverBascula.SelectedItem = sel;
            else if (CmbDriverBascula.SelectedItem == null && CmbDriverBascula.Items.Count > 0)
                CmbDriverBascula.SelectedIndex = 0;

            bool detectada = puertos.Any();
            if (detectada)
            {
                BadgeEstadoBascula.Background = new SolidColorBrush(Color.FromRgb(22, 163, 74));
                TxtEstadoBascula.Text = $"Báscula detectada automáticamente en {puertos.First()}";
            }
            else
            {
                BadgeEstadoBascula.Background = new SolidColorBrush(Color.FromRgb(153, 27, 27));
                TxtEstadoBascula.Text = "Báscula no detectada (esperando conexión/driver)";
            }
        }

        private void BtnSoporteBascula_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Si tu báscula no aparece, verifica driver USB/Serial y reinicia el módulo.");
        }

        private void Config_Changed(object sender, RoutedEventArgs e)
        {
            if (_cargando)
                return;
            if (ChkBasculaActiva is null || TxtCodigoInicial is null || ChkEtiquetaPrecio is null ||
                ChkEtiquetaPeso is null || CmbDriverBascula is null)
                return;
            _cfg.Set("bascula_activa", ChkBasculaActiva.IsChecked == true ? "true" : "false");
            _cfg.Set("bascula_codigo_inicial", string.IsNullOrWhiteSpace(TxtCodigoInicial.Text) ? "2000" : TxtCodigoInicial.Text.Trim());
            _cfg.Set("bascula_etiqueta_precio", ChkEtiquetaPrecio.IsChecked == true ? "true" : "false");
            _cfg.Set("bascula_etiqueta_peso", ChkEtiquetaPeso.IsChecked == true ? "true" : "false");
            _cfg.Set("bascula_driver_puerto", CmbDriverBascula.SelectedItem?.ToString() ?? "");
        }
    }
}