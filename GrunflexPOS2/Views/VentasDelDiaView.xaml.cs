using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrunflexPOS2.Models;
using GrunflexPOS2.Models.Entities;
using GrunflexPOS2.Security;
using GrunflexPOS2.Services;
using Microsoft.VisualBasic;

namespace GrunflexPOS2.Views
{
    public partial class VentasDelDiaView : Window
    {
        private ObservableCollection<Venta> _ventas = new();
        private List<Venta> _filtroCompleto = new();
        private bool _cargandoFiltros;
        private readonly CultureInfo _cl = new("es-CL");
        private int _pageSize = 10;
        private int _currentPage = 1;

        public VentasDelDiaView()
        {
            InitializeComponent();

            _ventas = new ObservableCollection<Venta>(App.VentaService.ObtenerVentas().OrderByDescending(v => v.Fecha));
            CargarFiltros();

            TxtBuscar.Text = "Buscar por número de ticket...";
            TxtBuscar.Foreground = Brushes.Gray;

            AplicarFiltros();
            LimpiarDetalle();
            BtnDevolverArticulo.IsEnabled = false;
            AplicarPermisosUi();
        }

        private void AplicarPermisosUi()
        {
            BtnCancelarVenta.IsEnabled = UsuarioPermisos.PuedeCancelarTickets();
            BtnDevolverArticulo.IsEnabled = false;
        }

        private void Cerrar_Click(object sender, RoutedEventArgs e) =>
            Close();

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

        private void TxtBuscar_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtBuscar.Text == "Buscar por número de ticket...")
                return;
            _currentPage = 1;
            AplicarFiltros();
        }

        private void CargarFiltros()
        {
            _cargandoFiltros = true;
            DpFecha.SelectedDate = DateTime.Today;

            var cajas = _ventas.Select(v => v.NumeroCaja.ToString()).Distinct().OrderBy(x => x).ToList();
            cajas.Insert(0, "Todas");
            CmbCaja.ItemsSource = cajas;
            CmbCaja.SelectedIndex = 0;

            var cajeros = _ventas.Select(v => v.Cajero ?? "").Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct().OrderBy(x => x).ToList();
            cajeros.Insert(0, "Todos");
            CmbCajero.ItemsSource = cajeros;
            CmbCajero.SelectedIndex = 0;
            _cargandoFiltros = false;
        }

        private void Filtro_Changed(object sender, RoutedEventArgs e)
        {
            if (_cargandoFiltros)
                return;
            _currentPage = 1;
            AplicarFiltros();
        }

        private void AplicarFiltros()
        {
            var q = TxtBuscar.Text.Trim();
            bool conPlaceholder = q == "Buscar por número de ticket...";
            if (conPlaceholder)
                q = string.Empty;

            DateTime? fecha = DpFecha.SelectedDate?.Date;
            string caja = CmbCaja.SelectedItem?.ToString() ?? "Todas";
            string cajero = CmbCajero.SelectedItem?.ToString() ?? "Todos";
            bool soloCredito = ChkVentasCredito.IsChecked == true;

            _filtroCompleto = _ventas.Where(v =>
            {
                if (fecha.HasValue && v.Fecha.Date != fecha.Value)
                    return false;
                if (!string.Equals(caja, "Todas", StringComparison.OrdinalIgnoreCase) &&
                    v.NumeroCaja.ToString() != caja)
                    return false;
                if (!string.Equals(cajero, "Todos", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(v.Cajero ?? "", cajero, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (soloCredito && !(v.MetodoPago ?? "").Contains("credito", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (string.IsNullOrWhiteSpace(q))
                    return true;
                return v.NumeroTicket.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)
                       || (v.Cliente ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                       || (v.Cajero ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);
            }).OrderByDescending(v => v.Fecha).ToList();

            RefrescarListaPagina();
        }

        private void RefrescarListaPagina()
        {
            int total = _filtroCompleto.Count;
            int totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)_pageSize));
            if (_currentPage > totalPages)
                _currentPage = totalPages;
            if (_currentPage < 1)
                _currentPage = 1;

            var slice = _filtroCompleto.Skip((_currentPage - 1) * _pageSize).Take(_pageSize).ToList();
            ListaVentas.ItemsSource = slice;

            if (total == 0)
                TxtListaPagina.Text = "Sin ventas";
            else
            {
                int start = (_currentPage - 1) * _pageSize + 1;
                int end = Math.Min(_currentPage * _pageSize, total);
                TxtListaPagina.Text = $"{start}–{end} · Pág. {_currentPage}/{totalPages}";
            }

            BtnListaPrev.IsEnabled = _currentPage > 1;
            BtnListaSig.IsEnabled = _currentPage < totalPages;
        }

        private void BtnListaPrev_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage <= 1)
                return;
            _currentPage--;
            RefrescarListaPagina();
        }

        private void BtnListaSig_Click(object sender, RoutedEventArgs e)
        {
            int totalPages = Math.Max(1, (int)Math.Ceiling(_filtroCompleto.Count / (double)_pageSize));
            if (_currentPage >= totalPages)
                return;
            _currentPage++;
            RefrescarListaPagina();
        }

        private void ListaVentas_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListaVentas.SelectedItem is Venta venta)
            {
                TxtTituloTicket.Text = venta.EsConsumoPersonal
                    ? $"CONSUMO PERSONAL N° {venta.NumeroTicket:D6}"
                    : $"TICKET N° {venta.NumeroTicket:D6}";
                TxtInfoFolio.Text = $"Folio {venta.NumeroTicket}";
                TxtInfoCajero.Text = $"Cajero {venta.Cajero}";
                TxtInfoCliente.Text = $"Cliente {venta.Cliente}";
                TxtInfoFecha.Text = venta.Fecha.ToString("dd/MM/yyyy HH:mm");
                TxtMetodoPago.Text = $"Pago con {venta.MetodoPago}";
                TxtTotalVenta.Text = venta.Total.ToString("C2", _cl);
                TxtResumenArticulos.Text = $"Total de artículos: {venta.TotalArticulos}";
                ListaDetalle.ItemsSource = venta.Items;
                BtnDevolverArticulo.IsEnabled =
                    venta.Items != null && venta.Items.Count > 0 && !venta.EstaAnulada;
            }
            else
                LimpiarDetalle();
        }

        private void LimpiarDetalle()
        {
            TxtTituloTicket.Text = "TICKET N° —";
            TxtInfoFolio.Text = "Folio —";
            TxtInfoCajero.Text = "Cajero —";
            TxtInfoCliente.Text = "Cliente —";
            TxtInfoFecha.Text = "";
            TxtMetodoPago.Text = "";
            TxtTotalVenta.Text = 0m.ToString("C2", _cl);
            TxtResumenArticulos.Text = "Total de artículos: 0";
            ListaDetalle.ItemsSource = null;
            BtnDevolverArticulo.IsEnabled = false;
        }

        private void CancelarVenta_Click(object sender, RoutedEventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureCancelarTickets())
                return;

            if (ListaVentas.SelectedItem is Venta venta)
            {
                var confirm = MessageBox.Show(
                    $"¿Cancelar Ticket N° {venta.NumeroTicket}?",
                    "Confirmar cancelación",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirm == MessageBoxResult.Yes)
                {
                    App.VentaService.AnularVenta(venta.NumeroTicket);
                    _ventas = new ObservableCollection<Venta>(App.VentaService.ObtenerVentas().OrderByDescending(v => v.Fecha));
                    AplicarFiltros();
                    LimpiarDetalle();
                }
            }
        }

        private void DevolverArticulo_Click(object sender, RoutedEventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureDevolverArticulos())
                return;

            if (ListaVentas.SelectedItem is not Venta venta || ListaDetalle.SelectedItem is not DetalleVenta det)
            {
                MessageBox.Show("Selecciona un artículo para devolver.");
                return;
            }

            string qtyText = Interaction.InputBox("Cantidad a devolver:", "Devolución", "1");
            if (!int.TryParse(qtyText, out int qty) || qty <= 0)
            {
                MessageBox.Show("Cantidad inválida.");
                return;
            }

            if (!App.VentaService.DevolverArticulo(venta.NumeroTicket, det.Producto, det.Precio, qty, out string mensaje))
            {
                MessageBox.Show(mensaje);
                return;
            }

            MessageBox.Show(mensaje);
            int ticket = venta.NumeroTicket;
            _ventas = new ObservableCollection<Venta>(App.VentaService.ObtenerVentas().OrderByDescending(v => v.Fecha));

            AplicarFiltros();

            var actualizada = _filtroCompleto.FirstOrDefault(v => v.NumeroTicket == ticket);
            if (actualizada != null)
            {
                int idx = _filtroCompleto.IndexOf(actualizada);
                _currentPage = idx / _pageSize + 1;
                RefrescarListaPagina();
                ListaVentas.SelectedItem =
                    ListaVentas.Items.Cast<Venta>().FirstOrDefault(v => v.NumeroTicket == ticket);
            }
        }

        private void ImprimirCopia_Click(object sender, RoutedEventArgs e)
        {
            if (ListaVentas.SelectedItem is Venta venta)
                TicketPdfService.GenerarTicketPDF(venta);
        }
    }
}
