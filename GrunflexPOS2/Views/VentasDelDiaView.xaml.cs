using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrunflexPOS2.Models;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Views
{
    public partial class VentasDelDiaView : Window
    {
        private ObservableCollection<Venta> _ventas;

        public VentasDelDiaView()
        {
            InitializeComponent();

            _ventas = new ObservableCollection<Venta>(App.VentaService.ObtenerVentas());
            VentasGrid.ItemsSource = _ventas;

            // Inicializa placeholder manual
            TxtBuscar.Text = "Buscar por número de ticket...";
            TxtBuscar.Foreground = Brushes.Gray;
        }

        // ================= CERRAR =================
        private void Cerrar_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ================= PLACEHOLDER =================
        private void TxtBuscar_GotFocus(object sender, RoutedEventArgs e)
        {
            if (TxtBuscar.Text == "Buscar por número de ticket...")
            {
                TxtBuscar.Text = "";
                TxtBuscar.Foreground = Brushes.Black;
            }
        }

        private void TxtBuscar_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtBuscar.Text))
            {
                TxtBuscar.Text = "Buscar por número de ticket...";
                TxtBuscar.Foreground = Brushes.Gray;
            }
        }

        // ================= FILTRO =================
        private void TxtBuscar_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtBuscar.Text == "Buscar por número de ticket...")
                return;

            var texto = TxtBuscar.Text.Trim();

            if (string.IsNullOrWhiteSpace(texto))
            {
                VentasGrid.ItemsSource = _ventas;
            }
            else
            {
                var filtradas = _ventas
                    .Where(v => v.NumeroTicket.ToString().Contains(texto))
                    .ToList();

                VentasGrid.ItemsSource = filtradas;
            }
        }

        // ================= SELECCIÓN =================
        private void VentasGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VentasGrid.SelectedItem is Venta venta)
            {
                TxtTituloTicket.Text = $"Ticket N° {venta.NumeroTicket}";
                TxtInfoFolio.Text = $"Folio: {venta.NumeroTicket}";
                TxtInfoCajero.Text = $"Cajero: {venta.Cajero}";
                TxtInfoCliente.Text = $"Cliente: {venta.Cliente}";
                TxtInfoFecha.Text = $"Fecha: {venta.Fecha:dd/MM/yyyy HH:mm}";
                TxtMetodoPago.Text = $"Pago con: {venta.MetodoPago}";
                TxtTotalVenta.Text = $"Total: {venta.Total:C}";
                DetalleGrid.ItemsSource = venta.Items;
            }
        }

        // ================= CANCELAR VENTA =================
        private void CancelarVenta_Click(object sender, RoutedEventArgs e)
        {
            if (VentasGrid.SelectedItem is Venta venta)
            {
                var confirm = MessageBox.Show(
                    $"¿Cancelar Ticket N° {venta.NumeroTicket}?",
                    "Confirmar cancelación",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirm == MessageBoxResult.Yes)
                {
                    App.VentaService.AnularVenta(venta.NumeroTicket);

                    _ventas.Remove(venta);

                    // Limpia panel derecho
                    TxtTituloTicket.Text = "Ticket N°";
                    TxtInfoFolio.Text = "";
                    TxtInfoCajero.Text = "";
                    TxtInfoCliente.Text = "";
                    TxtInfoFecha.Text = "";
                    TxtMetodoPago.Text = "";
                    TxtTotalVenta.Text = "";
                    DetalleGrid.ItemsSource = null;
                }
            }
        }

        // ================= IMPRIMIR COPIA =================
        private void ImprimirCopia_Click(object sender, RoutedEventArgs e)
        {
            if (VentasGrid.SelectedItem is Venta venta)
            {
                MessageBox.Show(
                    $"Imprimiendo copia del Ticket N° {venta.NumeroTicket}",
                    "Impresión",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
    }
}