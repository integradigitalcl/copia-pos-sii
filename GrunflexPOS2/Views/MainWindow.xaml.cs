using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Connectivity;
using GrunflexPOS2.Views;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GrunflexPOS2
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // 🔥 CARGA CORRECTA (DESPUÉS DEL RENDER)
            Loaded += MainWindow_Loaded;

            // 🔥 FORZAR EVENTO INVENTARIO (SOLUCIÓN AL BUG)
            Loaded += (s, e) =>
            {
                BuscarBotonInventario(this);
            };

            // Suscripción al monitor de conectividad para reflejar estado en footer.
            if (App.Connectivity != null)
            {
                App.Connectivity.StateChanged += App_ConnectivityChanged;
                ActualizarIndicadorConexion(App.Connectivity.State);
            }
            Closed += (_, _) =>
            {
                if (App.Connectivity != null)
                    App.Connectivity.StateChanged -= App_ConnectivityChanged;
            };
        }

        private void App_ConnectivityChanged(object? sender, ConnectivityState state)
        {
            Dispatcher.BeginInvoke(new Action(() => ActualizarIndicadorConexion(state)));
        }

        private void ActualizarIndicadorConexion(ConnectivityState state)
        {
            switch (state)
            {
                case ConnectivityState.Online:
                    EstadoConexionDot.Fill = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
                    EstadoConexionTxt.Text = $"Conectado ({App.Connectivity?.LastLatencyMs ?? 0} ms)";
                    break;
                case ConnectivityState.Degraded:
                    EstadoConexionDot.Fill = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
                    EstadoConexionTxt.Text = $"Lento ({App.Connectivity?.LastLatencyMs ?? 0} ms)";
                    break;
                case ConnectivityState.Offline:
                    EstadoConexionDot.Fill = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
                    EstadoConexionTxt.Text = "Sin conexión";
                    break;
                default:
                    EstadoConexionDot.Fill = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
                    EstadoConexionTxt.Text = "Conexión: ...";
                    break;
            }
        }

        private void EstadoConexion_Click(object sender, MouseButtonEventArgs e)
        {
            // Atajo: abre el diagnóstico para que el usuario vea qué pasa con la conexión.
            try
            {
                var dlg = new DiagnosticoView { Owner = this };
                dlg.ShowDialog();
            }
            catch { }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            MainContent.Content = new DashboardView();
            CloudBackupCoordinator.TryStartMainWindow();
        }

        private void CambiarVista(UserControl vista)
        {
            // 🔥 FORZAR REFRESCO REAL
            MainContent.Content = null;
            MainContent.UpdateLayout();
            MainContent.Content = vista;
        }

        private void BtnVentas_Click(object sender, RoutedEventArgs e)
        {
            CambiarVista(new VentasView());
        }

        private void BtnProductos_Click(object sender, RoutedEventArgs e)
        {
            CambiarVista(new ProductosView());
        }

        private void BtnInventario_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CambiarVista(new InventarioView());
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error cargando Inventario:\n" + ex.Message);
            }
        }

        private void BtnConfiguracion_Click(object sender, RoutedEventArgs e)
        {
            CambiarVista(new ConfiguracionView());
        }

        // 🔥 TECLAS
        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F1)
                CambiarVista(new VentasView());

            if (e.Key == Key.F2)
                CambiarVista(new ProductosView());

            if (e.Key == Key.F4)
                CambiarVista(new InventarioView());

            // F10: en inventario solo si el foco está en el panel de inventario (búsqueda); si el foco está en el menú, ir a Configuración.
            if (EsTeclaF10(e))
            {
                if (MainContent?.Content is InventarioView inv && inv.IsKeyboardFocusWithin)
                    return;
                CambiarVista(new ConfiguracionView());
                e.Handled = true;
            }
        }

        // 🔥 MÉTODO NUEVO (NO EXISTÍA)
        private static bool EsTeclaF10(KeyEventArgs e) =>
            e.Key == Key.F10 || (e.Key == Key.System && e.SystemKey == Key.F10);

        private void BuscarBotonInventario(object obj)
        {
            if (obj is Button btn)
            {
                if (btn.Content != null && btn.Content.ToString().Contains("Inventario"))
                {
                    btn.Click -= BtnInventario_Click; // evitar duplicados
                    btn.Click += BtnInventario_Click;
                }
            }

            if (obj is DependencyObject dep)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(dep))
                {
                    BuscarBotonInventario(child);
                }
            }
        }
    }
}