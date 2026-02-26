using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using GrunflexPOS2.Models;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Views
{
    public partial class CajaView : Window
    {
        private VentasView? _ventasView;
        private decimal _totalActual = 0;

        public CajaView()
        {
            InitializeComponent();
            CargarVentasInicial();
        }

        private void CargarVentasInicial()
        {
            _ventasView = new VentasView();
            _ventasView.ResumenActualizado += VentasView_ResumenActualizado;
            MainContent.Content = _ventasView;
        }

        // ================= ATAJOS =================
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

        // ================= ACTUALIZA RESUMEN =================
        private void VentasView_ResumenActualizado(decimal subtotal, int articulos)
        {
            _totalActual = subtotal;

            ResumenArticulos.Text = articulos.ToString();
            ResumenSubtotal.Text = subtotal.ToString("C");
            TotalText.Text = subtotal.ToString("C");
        }

        // ================= BOTÓN COBRAR =================
        private void Cobrar_Click(object sender, RoutedEventArgs e)
        {
            AbrirCobro();
        }

        // ================= BOTÓN VENTAS DEL DÍA =================
        private void VentasDelDia_Click(object sender, RoutedEventArgs e)
        {
            var ventana = new VentasDelDiaView();
            ventana.Owner = this;
            ventana.ShowDialog();
        }

        // ================= REIMPRIMIR ÚLTIMO =================
        private void ReimprimirUltimo_Click(object sender, RoutedEventArgs e)
        {
            var ventas = VentasService.ObtenerVentas();

            if (ventas.Count == 0)
            {
                MessageBox.Show(
                    "No hay ventas para reimprimir.",
                    "Aviso",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var ultimaVenta = ventas.Last();

            TicketPdfService.GenerarTicketPDF(ultimaVenta);

            MessageBox.Show(
                $"Ticket N° {ultimaVenta.NumeroTicket} reimpreso correctamente.",
                "Reimpresión exitosa",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // ================= LÓGICA DE COBRO =================
        private void AbrirCobro()
        {
            if (_ventasView == null || _totalActual <= 0)
                return;

            var ventana = new CobroView(_totalActual);
            ventana.Owner = this;

            if (ventana.ShowDialog() == true)
            {
                // 🔥 Generar número de ticket
                int numeroTicket = VentasService.GenerarNumeroTicket();

                // 🔥 Crear objeto venta (CORREGIDO)
                var nuevaVenta = new Venta
                {
                    NumeroTicket = numeroTicket,
                    Fecha = DateTime.Now,
                    Total = _totalActual,
                    Items = _ventasView.ObtenerItemsActuales().ToList()
                };

                // 🔥 Guardar en memoria
                VentasService.GuardarVenta(nuevaVenta);

                // 🔥 Generar PDF
                TicketPdfService.GenerarTicketPDF(nuevaVenta);

                MessageBox.Show(
                    $"Venta guardada correctamente.\nTicket N° {numeroTicket}",
                    "Venta exitosa",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                // 🔥 Limpiar venta
                _ventasView.LimpiarVenta();

                ResumenArticulos.Text = "0";
                ResumenSubtotal.Text = "$0";
                TotalText.Text = "$0";
                _totalActual = 0;
            }
        }

        // ================= BOTONES SUPERIORES =================
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

        // ================= MENÚ IZQUIERDO =================
        private void Ventas_Click(object sender, RoutedEventArgs e)
        {
            CargarVentasInicial();
        }

        private void Productos_Click(object sender, RoutedEventArgs e) { }
        private void Inventario_Click(object sender, RoutedEventArgs e) { }
        private void Reportes_Click(object sender, RoutedEventArgs e) { }
        private void Corte_Click(object sender, RoutedEventArgs e) { }

        private void Web_Click(object sender, RoutedEventArgs e)
        {
            var ventana = new WebViewWindow("https://www.grunflex.cl");
            ventana.Owner = this;
            ventana.ShowDialog();
        }

        private void Compras_Click(object sender, RoutedEventArgs e) { }
        private void Configuracion_Click(object sender, RoutedEventArgs e) { }
    }
}
