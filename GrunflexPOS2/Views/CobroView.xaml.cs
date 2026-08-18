using System;
using GrunflexPOS2.Security;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GrunflexPOS2.Services;
using GrunflexPOS2.UI;

namespace GrunflexPOS2.Views
{
    public partial class CobroView : Window
    {
        private static readonly SolidColorBrush CobroAzulSeleccion = CreateFrozen(Color.FromRgb(0, 86, 210));
        private static readonly SolidColorBrush CobroGrisTexto = CreateFrozen(Color.FromRgb(107, 114, 128));
        private static readonly SolidColorBrush CobroBordeClaro = CreateFrozen(Color.FromRgb(229, 231, 235));

        private static SolidColorBrush CreateFrozen(Color c)
        {
            var b = new SolidColorBrush(c);
            if (b.CanFreeze)
                b.Freeze();
            return b;
        }

        private decimal _total;
        private readonly ConfiguracionService _cfg = new();
        private string _metodoPagoActual = "Efectivo";
        public bool EditarCompraSolicitada { get; private set; }
        public decimal? DescuentoSolicitadoPorcentaje { get; private set; }
        public string MetodoPagoSeleccionado => _metodoPagoActual;
        public bool ImprimirTicket { get; private set; } = true;

        /// <summary>Salida de stock sin cobro ni movimiento de caja.</summary>
        public bool ConsumoPersonal { get; private set; }

        public CobroView(
            decimal total,
            string? metodoInicial = null,
            int totalArticulos = 0,
            decimal descuentosAplicados = 0)
        {
            InitializeComponent();

            _total = total;
            var cultura = CultureInfo.CurrentCulture;
            TxtCurrencyBadge.Text = string.IsNullOrWhiteSpace(cultura.NumberFormat.CurrencySymbol)
                ? "$"
                : cultura.NumberFormat.CurrencySymbol.Trim();

            TxtTotalCobro.Text = total.ToString("C", cultura);
            TxtTotalBarra.Text = total.ToString("C", cultura);
            TxtTotalArticulos.Text = totalArticulos.ToString(cultura);
            TxtDescuentosBarra.Text = descuentosAplicados.ToString("C", cultura);

            TxtCambio.Text = 0m.ToString("C", cultura);

            AplicarConfigFormasPago();
            SeleccionarMetodoInicial(metodoInicial);
        }

        private void AplicarConfigFormasPago()
        {
            BtnTarjeta.Visibility = _cfg.Get("pago_tarjeta_habilitada") == "false" ? Visibility.Collapsed : Visibility.Visible;
            BtnTransferencia.Visibility = _cfg.Get("pago_transferencia_habilitada") == "true" ? Visibility.Visible : Visibility.Collapsed;
            BtnMixto.Visibility = _cfg.Get("pago_mixto_habilitado") == "false" ? Visibility.Collapsed : Visibility.Visible;
            BtnCheque.Visibility = _cfg.Get("pago_cheque_habilitado") == "true" ? Visibility.Visible : Visibility.Collapsed;
            BtnVales.Visibility = _cfg.Get("pago_vales_habilitado") == "true" ? Visibility.Visible : Visibility.Collapsed;
            BtnDolares.Visibility = _cfg.Get("pago_dolar_habilitado") == "true" ? Visibility.Visible : Visibility.Collapsed;

            bool hayExtra = BtnTransferencia.Visibility == Visibility.Visible
                || BtnCheque.Visibility == Visibility.Visible
                || BtnVales.Visibility == Visibility.Visible
                || BtnDolares.Visibility == Visibility.Visible;
            PanelMetodosExtra.Visibility = hayExtra ? Visibility.Visible : Visibility.Collapsed;
        }

        // ================= TECLAS =================
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }

            if (e.Key == Key.F1)
            {
                ImprimirTicket = true;
                ConfirmarPago();
                e.Handled = true;
            }

            if (e.Key == Key.F2)
            {
                ImprimirTicket = false;
                ConfirmarPago();
                e.Handled = true;
            }

            if (e.Key == Key.F3)
            {
                ConfirmarConsumoPersonal();
                e.Handled = true;
            }

            if (e.Key is Key.Enter or Key.Return)
            {
                ImprimirTicket = true;
                ConfirmarPago();
                e.Handled = true;
            }
        }

        // ================= MÉTODOS DE PAGO =================
        private void MetodoPago_Click(object sender, RoutedEventArgs e)
        {
            PanelEfectivo.Visibility = Visibility.Collapsed;
            PanelTarjeta.Visibility = Visibility.Collapsed;
            PanelMixto.Visibility = Visibility.Collapsed;
            _metodoPagoActual = (sender as FrameworkElement)?.Tag?.ToString() ?? "Efectivo";

            if (sender == BtnEfectivo)
                PanelEfectivo.Visibility = Visibility.Visible;

            if (sender == BtnTarjeta || sender == BtnTransferencia || sender == BtnCheque || sender == BtnVales || sender == BtnDolares)
                PanelTarjeta.Visibility = Visibility.Visible;

            if (sender == BtnMixto)
                PanelMixto.Visibility = Visibility.Visible;

            ActualizarEtiquetaMontoRecibido();
            RefreshMetodoVisual();
        }

        private void ActualizarEtiquetaMontoRecibido()
        {
            if (PanelEfectivo.Visibility == Visibility.Visible || PanelMixto.Visibility == Visibility.Visible)
                LblMontoRecibido.Text = "MONTO RECIBIDO";
            else
                LblMontoRecibido.Text = "REFERENCIA (OPCIONAL)";
        }

        private void RefreshMetodoVisual()
        {
            bool Sel(string tag) =>
                string.Equals(_metodoPagoActual, tag, StringComparison.OrdinalIgnoreCase);

            ApplyMetodoStyle(BtnEfectivo, Sel("Efectivo"));
            ApplyMetodoStyle(BtnTarjeta, Sel("Tarjeta"));
            ApplyMetodoStyle(BtnMixto, Sel("Mixto"));
            ApplyMetodoStyle(BtnTransferencia, Sel("Transferencia"));
            ApplyMetodoStyle(BtnCheque, Sel("Cheque"));
            ApplyMetodoStyle(BtnVales, Sel("Vales"));
            ApplyMetodoStyle(BtnDolares, Sel("Dolares"));
        }

        private static void ApplyMetodoStyle(Button btn, bool selected)
        {
            if (selected)
            {
                btn.Background = CobroAzulSeleccion;
                btn.Foreground = Brushes.White;
                btn.BorderBrush = CobroAzulSeleccion;
            }
            else
            {
                btn.Background = Brushes.White;
                btn.Foreground = CobroGrisTexto;
                btn.BorderBrush = CobroBordeClaro;
            }
        }

        // ================= EFECTIVO =================
        private void TxtEfectivo_TextChanged(object sender, TextChangedEventArgs e)
        {
            var cultura = CultureInfo.CurrentCulture;
            if (TryParseMoney(TxtEfectivo.Text, out decimal recibido))
            {
                decimal cambio = recibido - _total;

                TxtCambio.Text = cambio >= 0
                    ? cambio.ToString("C", cultura)
                    : 0m.ToString("C", cultura);
            }
            else
            {
                TxtCambio.Text = 0m.ToString("C", cultura);
            }
        }

        // ================= MIXTO =================
        private void TxtMixto_TextChanged(object sender, TextChangedEventArgs e)
        {
            var cultura = CultureInfo.CurrentCulture;
            TryParseMoney(TxtMixtoEfectivo.Text, out decimal efectivo);
            TryParseMoney(TxtMixtoTarjeta.Text, out decimal tarjeta);

            decimal totalRecibido = efectivo + tarjeta;
            decimal cambio = totalRecibido - _total;

            TxtCambio.Text = cambio >= 0
                ? cambio.ToString("C", cultura)
                : 0m.ToString("C", cultura);
        }

        private bool TryParseMoney(string? text, out decimal value)
        {
            var cultura = CultureInfo.CurrentCulture;
            if (!string.IsNullOrWhiteSpace(text)
                && (decimal.TryParse(text, NumberStyles.Any, cultura, out value)
                    || decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value)))
                return true;
            value = 0;
            return false;
        }

        // ================= BOTONES =================
        private void BtnCobrarImprimir_Click(object sender, RoutedEventArgs e)
        {
            ImprimirTicket = true;
            ConfirmarPago();
        }

        private void BtnCobrarSinImprimir_Click(object sender, RoutedEventArgs e)
        {
            ImprimirTicket = false;
            ConfirmarPago();
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void BtnConsumoPersonal_Click(object sender, RoutedEventArgs e)
        {
            ConfirmarConsumoPersonal();
        }

        private void ConfirmarConsumoPersonal()
        {
            var r = MessageBox.Show(
                "Se descontará el inventario de los productos listados, sin ingreso en caja. El movimiento quedará registrado en reportes y corte como consumo personal (total monetario $0). ¿Confirmar?",
                "Consumo personal",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (r != MessageBoxResult.Yes)
                return;

            ConsumoPersonal = true;
            ImprimirTicket = false;
            DialogResult = true;
        }

        private void SeleccionarMetodoInicial(string? metodoInicial)
        {
            var m = (metodoInicial ?? string.Empty).Trim().ToLowerInvariant();
            if (m == "tarjeta" && BtnTarjeta.Visibility == Visibility.Visible) { MetodoPago_Click(BtnTarjeta, new RoutedEventArgs()); return; }
            if (m == "transferencia" && BtnTransferencia.Visibility == Visibility.Visible) { MetodoPago_Click(BtnTransferencia, new RoutedEventArgs()); return; }
            if (m == "mixto" && BtnMixto.Visibility == Visibility.Visible) { MetodoPago_Click(BtnMixto, new RoutedEventArgs()); return; }
            if (m == "cheque" && BtnCheque.Visibility == Visibility.Visible) { MetodoPago_Click(BtnCheque, new RoutedEventArgs()); return; }
            if (m == "vales" && BtnVales.Visibility == Visibility.Visible) { MetodoPago_Click(BtnVales, new RoutedEventArgs()); return; }
            if ((m == "dolares" || m == "dólares") && BtnDolares.Visibility == Visibility.Visible) { MetodoPago_Click(BtnDolares, new RoutedEventArgs()); return; }
            MetodoPago_Click(BtnEfectivo, new RoutedEventArgs());
        }

        private void BtnEditarCompra_Click(object sender, RoutedEventArgs e)
        {
            EditarCompraSolicitada = true;
            DialogResult = false;
        }

        private void BtnAplicarDescuento_Click(object sender, RoutedEventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureDescuentos())
                return;

            string raw = GrunflexDialogs.Input(
                "Ingrese descuento global (% entre 0 y 100):",
                "Aplicar descuento",
                "0");

            if (string.IsNullOrWhiteSpace(raw))
                return;

            raw = raw.Trim().Replace("%", "").Replace(",", ".");
            if (!decimal.TryParse(raw, NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var porcentaje))
            {
                GrunflexDialogs.Show("Descuento inválido.");
                return;
            }

            if (porcentaje < 0 || porcentaje > 100)
            {
                GrunflexDialogs.Show("El descuento debe estar entre 0 y 100.");
                return;
            }

            DescuentoSolicitadoPorcentaje = porcentaje;
            DialogResult = false;
        }

        // ================= CONFIRMAR =================
        private void ConfirmarPago()
        {
            decimal recibido = 0;

            if (PanelEfectivo.Visibility == Visibility.Visible)
            {
                if (string.IsNullOrWhiteSpace(TxtEfectivo.Text))
                {
                    recibido = _total;
                }
                else
                {
                    TryParseMoney(TxtEfectivo.Text, out recibido);
                }
            }
            else if (PanelMixto.Visibility == Visibility.Visible)
            {
                TryParseMoney(TxtMixtoEfectivo.Text, out decimal efectivo);
                TryParseMoney(TxtMixtoTarjeta.Text, out decimal tarjeta);

                recibido = efectivo + tarjeta;
            }
            else
            {
                recibido = _total;
            }

            if (recibido >= _total)
            {
                DialogResult = true;
            }
            else
            {
                if (_cfg.Get("pago_efectivo_no_menor") == "false")
                {
                    DialogResult = true;
                    return;
                }
                MessageBox.Show("El monto recibido es insuficiente.");
            }
        }
    }
}
