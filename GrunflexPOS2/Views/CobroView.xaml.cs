using System;
using System.Windows;
using System.Windows.Input;

namespace GrunflexPOS2.Views
{
    public partial class CobroView : Window
    {
        private decimal _total;

        public CobroView(decimal total)
        {
            InitializeComponent();

            _total = total;
            TxtTotalCobro.Text = total.ToString("C");
            TxtCambio.Text = "$0";
        }

        // ================= TECLAS =================
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
            }

            if (e.Key == Key.F1)
            {
                ConfirmarPago();
            }

            if (e.Key == Key.F2)
            {
                ConfirmarPago();
            }
        }

        // ================= MÉTODOS DE PAGO =================
        private void MetodoPago_Click(object sender, RoutedEventArgs e)
        {
            PanelEfectivo.Visibility = Visibility.Collapsed;
            PanelTarjeta.Visibility = Visibility.Collapsed;
            PanelMixto.Visibility = Visibility.Collapsed;

            if (sender == BtnEfectivo)
                PanelEfectivo.Visibility = Visibility.Visible;

            if (sender == BtnTarjeta || sender == BtnTransferencia)
                PanelTarjeta.Visibility = Visibility.Visible;

            if (sender == BtnMixto)
                PanelMixto.Visibility = Visibility.Visible;
        }

        // ================= EFECTIVO =================
        private void TxtEfectivo_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (decimal.TryParse(TxtEfectivo.Text, out decimal recibido))
            {
                decimal cambio = recibido - _total;

                TxtCambio.Text = cambio >= 0
                    ? cambio.ToString("C")
                    : "$0";
            }
            else
            {
                TxtCambio.Text = "$0";
            }
        }

        // ================= MIXTO =================
        private void TxtMixto_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            decimal efectivo = 0;
            decimal tarjeta = 0;

            decimal.TryParse(TxtMixtoEfectivo.Text, out efectivo);
            decimal.TryParse(TxtMixtoTarjeta.Text, out tarjeta);

            decimal totalRecibido = efectivo + tarjeta;
            decimal cambio = totalRecibido - _total;

            TxtCambio.Text = cambio >= 0
                ? cambio.ToString("C")
                : "$0";
        }

        // ================= BOTONES =================
        private void BtnCobrarImprimir_Click(object sender, RoutedEventArgs e)
        {
            ConfirmarPago();
        }

        private void BtnCobrarSinImprimir_Click(object sender, RoutedEventArgs e)
        {
            ConfirmarPago();
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        // ================= CONFIRMAR =================
        private void ConfirmarPago()
        {
            decimal recibido = 0;

            if (PanelEfectivo.Visibility == Visibility.Visible)
            {
                decimal.TryParse(TxtEfectivo.Text, out recibido);
            }
            else if (PanelMixto.Visibility == Visibility.Visible)
            {
                decimal efectivo = 0;
                decimal tarjeta = 0;

                decimal.TryParse(TxtMixtoEfectivo.Text, out efectivo);
                decimal.TryParse(TxtMixtoTarjeta.Text, out tarjeta);

                recibido = efectivo + tarjeta;
            }
            else
            {
                // Tarjeta o transferencia
                recibido = _total;
            }

            if (recibido >= _total)
            {
                DialogResult = true;
            }
            else
            {
                MessageBox.Show("El monto recibido es insuficiente.");
            }
        }
    }
}
