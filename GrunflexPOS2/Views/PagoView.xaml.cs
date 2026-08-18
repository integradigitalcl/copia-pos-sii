using System.Windows;
using System.Globalization;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Views
{
    public partial class PagoView : Window
    {
        public string MetodoPago { get; private set; } = "";

        private decimal _total;
        private readonly ConfiguracionService _cfg = new();
        private readonly CultureInfo _cl = new("es-CL");

        public PagoView(decimal total)
        {
            InitializeComponent();

            _total = total;
            TotalText.Text = total.ToString("C0", _cl);
            AplicarConfigFormasPago();
        }

        private void AplicarConfigFormasPago()
        {
            BtnEfectivo.Visibility = Visibility.Visible;
            BtnTarjeta.Visibility = _cfg.Get("pago_tarjeta_habilitada") == "false" ? Visibility.Collapsed : Visibility.Visible;
            BtnTransferencia.Visibility = _cfg.Get("pago_transferencia_habilitada") == "true" ? Visibility.Visible : Visibility.Collapsed;
            BtnMixto.Visibility = _cfg.Get("pago_mixto_habilitado") == "false" ? Visibility.Collapsed : Visibility.Visible;
            BtnCheque.Visibility = _cfg.Get("pago_cheque_habilitado") == "true" ? Visibility.Visible : Visibility.Collapsed;
            BtnVales.Visibility = _cfg.Get("pago_vales_habilitado") == "true" ? Visibility.Visible : Visibility.Collapsed;
            BtnDolar.Visibility = _cfg.Get("pago_dolar_habilitado") == "true" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Metodo_Click(object sender, RoutedEventArgs e)
        {
            if (sender == BtnTarjeta) MetodoPago = "Tarjeta";
            else if (sender == BtnTransferencia) MetodoPago = "Transferencia";
            else if (sender == BtnMixto) MetodoPago = "Mixto";
            else if (sender == BtnCheque) MetodoPago = "Cheque";
            else if (sender == BtnVales) MetodoPago = "Vales";
            else if (sender == BtnDolar) MetodoPago = "Dolares";
            else MetodoPago = "Efectivo";
            DialogResult = true;
        }

        private void Cancelar_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}