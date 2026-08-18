using System;
using System.IO.Ports;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Views
{
    public partial class LectorCodigoView : UserControl
    {
        private readonly ConfiguracionService _cfg = new();
        private bool _cargando;

        public LectorCodigoView()
        {
            InitializeComponent();
            Loaded += LectorCodigoView_Loaded;
            Unloaded += LectorCodigoView_Unloaded;
        }

        private void LectorCodigoView_Loaded(object sender, RoutedEventArgs e)
        {
            LectorCodigoService.EstadoConexionCambiado += LectorCodigoService_EstadoConexionCambiado;
            _cargando = true;
            CmbBaudRate.ItemsSource = new[] { "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200" };
            CmbDataBits.ItemsSource = new[] { "5", "6", "7", "8" };
            CmbParity.ItemsSource = Enum.GetNames(typeof(Parity));
            CmbStopBits.ItemsSource = Enum.GetNames(typeof(StopBits)).Where(x => x != "None").ToList();
            CmbHandshake.ItemsSource = Enum.GetNames(typeof(Handshake));

            ChkLectorSerial.IsChecked = _cfg.Get("lector_serial_habilitado") == "true";

            RefrescarPuertos();
            Seleccionar(CmbPuerto, _cfg.Get("lector_serial_puerto"), CmbPuerto.Items.Count > 0 ? CmbPuerto.Items[0]?.ToString() : "COM1");
            Seleccionar(CmbBaudRate, _cfg.Get("lector_serial_baud"), "9600");
            Seleccionar(CmbDataBits, _cfg.Get("lector_serial_databits"), "8");
            Seleccionar(CmbParity, _cfg.Get("lector_serial_parity"), "None");
            Seleccionar(CmbStopBits, _cfg.Get("lector_serial_stopbits"), "One");
            Seleccionar(CmbHandshake, _cfg.Get("lector_serial_handshake"), "None");

            PanelSerial.Visibility = ChkLectorSerial.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            _cargando = false;
            ActualizarEstadoVisual(LectorCodigoService.EstaConectado, LectorCodigoService.PuertoActual);
        }

        private void LectorCodigoView_Unloaded(object sender, RoutedEventArgs e)
        {
            LectorCodigoService.EstadoConexionCambiado -= LectorCodigoService_EstadoConexionCambiado;
        }

        private void LectorCodigoService_EstadoConexionCambiado(bool conectado, string? puerto)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ActualizarEstadoVisual(conectado, puerto);
            }));
        }

        private void ActualizarEstadoVisual(bool conectado, string? puerto)
        {
            if (conectado)
            {
                BadgeEstadoLector.Background = new SolidColorBrush(Color.FromRgb(22, 163, 74));
                TxtEstadoLector.Text = $"Lector serial conectado ({puerto})";
            }
            else
            {
                BadgeEstadoLector.Background = new SolidColorBrush(Color.FromRgb(153, 27, 27));
                TxtEstadoLector.Text = string.IsNullOrWhiteSpace(puerto)
                    ? "Lector serial desconectado"
                    : $"Serial no disponible: {puerto}";
            }
        }

        private void ChkLectorSerial_Checked(object sender, RoutedEventArgs e)
        {
            RefrescarPuertos();
            PanelSerial.Visibility = Visibility.Visible;
            GuardarConfiguracion();
        }

        private void ChkLectorSerial_Unchecked(object sender, RoutedEventArgs e)
        {
            PanelSerial.Visibility = Visibility.Collapsed;
            GuardarConfiguracion();
            LectorCodigoService.Detener();
            ActualizarEstadoVisual(false, null);
        }

        private void BtnProbar_Click(object sender, RoutedEventArgs e)
        {
            GuardarConfiguracion();
            if (ChkLectorSerial.IsChecked != true)
            {
                MessageBox.Show("Activa la casilla del lector serial para probar.");
                return;
            }

            string puerto = CmbPuerto.SelectedItem?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(puerto))
            {
                MessageBox.Show(
                    "No hay puertos COM detectados.\n\n" +
                    "Si tu pistola es USB en modo teclado, desactiva esta casilla y úsala directamente en Ventas.",
                    "Lector serial",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            int baud = int.TryParse(CmbBaudRate.SelectedItem?.ToString(), out var b) ? b : 9600;
            int data = int.TryParse(CmbDataBits.SelectedItem?.ToString(), out var d) ? d : 8;
            var parity = Enum.TryParse<Parity>(CmbParity.SelectedItem?.ToString(), true, out var p) ? p : Parity.None;
            var stop = Enum.TryParse<StopBits>(CmbStopBits.SelectedItem?.ToString(), true, out var s) ? s : StopBits.One;
            var hand = Enum.TryParse<Handshake>(CmbHandshake.SelectedItem?.ToString(), true, out var h) ? h : Handshake.None;

            if (!LectorCodigoService.ProbarConexion(puerto, baud, data, parity, stop, hand, out string error))
            {
                MessageBox.Show("No se pudo abrir el lector:\n" + error);
                ActualizarEstadoVisual(false, null);
                return;
            }

            try
            {
                LectorCodigoService.Iniciar(puerto, baud, data, parity, stop, hand);
                ActualizarEstadoVisual(true, puerto);
                MessageBox.Show("Configuración correcta. Lector serial activo.");
            }
            catch (Exception ex)
            {
                ActualizarEstadoVisual(false, null);
                MessageBox.Show("Error al iniciar lector serial:\n" + ex.Message);
            }
        }

        private void SerialConfig_Changed(object sender, SelectionChangedEventArgs e) =>
            GuardarConfiguracion();

        private void BtnActualizarPuertos_Click(object sender, RoutedEventArgs e)
        {
            RefrescarPuertos();
        }

        private void RefrescarPuertos()
        {
            var puertos = LectorCodigoService.ObtenerPuertos();
            var seleccionado = CmbPuerto.SelectedItem?.ToString() ?? _cfg.Get("lector_serial_puerto");
            CmbPuerto.ItemsSource = puertos;
            Seleccionar(CmbPuerto, seleccionado, puertos.FirstOrDefault());

            var hayPuertos = puertos.Count > 0;
            BtnProbar.IsEnabled = hayPuertos;

            if (hayPuertos)
            {
                TxtInfoPuertos.Text = $"Puertos detectados: {string.Join(", ", puertos)}";
            }
            else
            {
                TxtInfoPuertos.Text =
                    "No se detectaron puertos COM. Para lector USB (modo teclado) no uses serial; " +
                    "escanea directamente en la pantalla de Ventas.";
            }
        }

        private void GuardarConfiguracion()
        {
            if (_cargando)
                return;

            _cfg.Set("lector_serial_habilitado", ChkLectorSerial.IsChecked == true ? "true" : "false");
            _cfg.Set("lector_serial_puerto", CmbPuerto.SelectedItem?.ToString() ?? "");
            _cfg.Set("lector_serial_baud", CmbBaudRate.SelectedItem?.ToString() ?? "9600");
            _cfg.Set("lector_serial_databits", CmbDataBits.SelectedItem?.ToString() ?? "8");
            _cfg.Set("lector_serial_parity", CmbParity.SelectedItem?.ToString() ?? "None");
            _cfg.Set("lector_serial_stopbits", CmbStopBits.SelectedItem?.ToString() ?? "One");
            _cfg.Set("lector_serial_handshake", CmbHandshake.SelectedItem?.ToString() ?? "None");
        }

        private static void Seleccionar(ComboBox combo, string valor, string? porDefecto)
        {
            string objetivo = string.IsNullOrWhiteSpace(valor) ? (porDefecto ?? string.Empty) : valor;
            var item = combo.Items.Cast<object>()
                .FirstOrDefault(x => string.Equals(x?.ToString(), objetivo, StringComparison.OrdinalIgnoreCase));
            combo.SelectedItem = item ?? combo.Items.Cast<object>().FirstOrDefault();
        }
    }
}