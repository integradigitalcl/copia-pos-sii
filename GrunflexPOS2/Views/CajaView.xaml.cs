using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GrunflexPOS2.Models;
using GrunflexPOS2.Services;
using GrunflexPOS2.Views;

namespace GrunflexPOS2.Views
{
    public partial class CajaView : Window
    {
        private VentasView? _ventasView;
        private decimal _totalActual = 0;
        private DispatcherTimer? _redTimer;

        public CajaView()
        {
            InitializeComponent();
            Loaded += CajaView_Loaded;
        }

        private void CajaView_Loaded(object sender, RoutedEventArgs e)
        {
            if (!CajaService.CajaAbierta())
            {
                var apertura = new AperturaCajaView();
                apertura.Owner = this;

                if (apertura.ShowDialog() != true)
                {
                    Close();
                    return;
                }
            }

            CargarVentasInicial();
            ActualizarBadgeCaja();
            IniciarMonitorRed();
        }

        private void ActualizarBadgeCaja()
        {
            var sesion = CajaService.SesionActual;
            if (sesion == null) return;

            IndicadorCajaText.Text =
                $"Caja {sesion.NumeroCaja} - {sesion.Cajero} - {sesion.FechaApertura:HH:mm} - ${sesion.MontoInicial:N0}";
        }

        private void IniciarMonitorRed()
        {
            _redTimer = new DispatcherTimer();
            _redTimer.Interval = TimeSpan.FromSeconds(5);
            _redTimer.Tick += (s, e) => VerificarRed();
            _redTimer.Start();
        }

        private void VerificarRed()
        {
            try
            {
                bool existe = Directory.Exists(@"\\DESKTOP-7VI49G5\GrunflexPOS");

                if (existe)
                {
                    BadgeRed.Background = new SolidColorBrush(Color.FromRgb(22, 163, 74));
                    EstadoRedText.Text = "Red OK";
                }
                else
                {
                    BadgeRed.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                    EstadoRedText.Text = "Sin Red";
                }
            }
            catch
            {
                BadgeRed.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                EstadoRedText.Text = "Sin Red";
            }
        }

        private void BtnCerrarCaja_Click(object sender, RoutedEventArgs e)
        {
            CajaService.CerrarCaja();
            BadgeCaja.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            IndicadorCajaText.Text = "Caja Cerrada";
        }

        private void CargarVentasInicial()
        {
            _ventasView = new VentasView();
            _ventasView.ResumenActualizado += VentasView_ResumenActualizado;
            MainContent.Content = _ventasView;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F10 || e.SystemKey == Key.F10)
            {
                _ventasView?.AbrirBusquedaDesdeAtajo();
                e.Handled = true;
            }

            if (e.Key == Key.F12)
            {
                AbrirCobro();
                e.Handled = true;
            }
        }

        private void VentasView_ResumenActualizado(decimal subtotal, int articulos)
        {
            _totalActual = subtotal;
            ResumenArticulos.Text = articulos.ToString();
            ResumenSubtotal.Text = subtotal.ToString("C");
            TotalText.Text = subtotal.ToString("C");
        }

        private void Cobrar_Click(object sender, RoutedEventArgs e)
        {
            AbrirCobro();
        }

        private void VentasDelDia_Click(object sender, RoutedEventArgs e)
        {
            var ventana = new VentasDelDiaView();
            ventana.Owner = this;
            ventana.ShowDialog();
        }

        private void ReimprimirUltimo_Click(object sender, RoutedEventArgs e)
        {
            var ventas = App.VentaService.ObtenerVentas();

            if (ventas.Count == 0)
            {
                MessageBox.Show("No hay ventas para reimprimir.");
                return;
            }

            var ultimaVenta = ventas.Last();
            TicketPdfService.GenerarTicketPDF(ultimaVenta);

            MessageBox.Show($"Ticket N° {ultimaVenta.NumeroTicket} reimpreso correctamente.");
        }

        private void AbrirCobro()
        {
            if (_ventasView == null || _totalActual <= 0)
                return;

            var ventana = new CobroView(_totalActual);
            ventana.Owner = this;

            if (ventana.ShowDialog() == true)
            {
                int numeroTicket = App.VentaService.GenerarNumeroTicket();

                var nuevaVenta = new Venta
                {
                    NumeroTicket = numeroTicket,
                    Fecha = DateTime.UtcNow, // 🔥 CORREGIDO PARA POSTGRESQL
                    Total = _totalActual,
                    Items = _ventasView.ObtenerItemsActuales().ToList()
                };

                App.VentaService.GuardarVenta(nuevaVenta);
                TicketPdfService.GenerarTicketPDF(nuevaVenta);

                _ventasView.LimpiarVenta();
                _totalActual = 0;
            }
        }

        private void Minimizar_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Maximizar_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void Cerrar_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BarraSuperior_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void Ventas_Click(object sender, RoutedEventArgs e) => CargarVentasInicial();
        private void Productos_Click(object sender, RoutedEventArgs e) { }
        private void Inventario_Click(object sender, RoutedEventArgs e) { }

        private void Reportes_Click(object sender, RoutedEventArgs e)
        {
            MainContent.Content = new ReportesView();
        }

        private void Corte_Click(object sender, RoutedEventArgs e)
        {
            if (!CajaService.CajaAbierta())
            {
                MessageBox.Show("No hay una caja abierta.");
                return;
            }

            var sesion = CajaService.SesionActual;
            if (sesion == null)
                return;

            var diaService = new DiaComercialService(new TimeSpan(8, 0, 0));

            var corteService = new CorteService(
                diaService,
                App.VentaService);

            var resumen = corteService.GenerarResumen(
                sesion.NumeroCaja.ToString(),
                sesion.Cajero,
                sesion.MontoInicial);

            MainContent.Content = new CorteView(resumen);
        }

        private void Compras_Click(object sender, RoutedEventArgs e) { }
        private void Configuracion_Click(object sender, RoutedEventArgs e) { }

        private void Web_Click(object sender, RoutedEventArgs e)
        {
            var ventana = new WebViewWindow("https://www.grunflex.cl");
            ventana.Owner = this;
            ventana.ShowDialog();
        }
    }
}