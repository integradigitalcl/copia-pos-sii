using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Views
{
    public partial class FormasPagoView : UserControl
    {
        private readonly ConfiguracionService _cfg = new();

        public FormasPagoView()
        {
            InitializeComponent();
            Cargar();
        }

        private void Cargar()
        {
            ChkEfectivoNoMenor.IsChecked = _cfg.Get("pago_efectivo_no_menor") != "false";
            ChkDolarHabilitado.IsChecked = _cfg.Get("pago_dolar_habilitado") == "true";
            TxtTipoCambio.Text = string.IsNullOrWhiteSpace(_cfg.Get("pago_dolar_tipo_cambio")) ? "1" : _cfg.Get("pago_dolar_tipo_cambio");

            ChkTarjetaHabilitada.IsChecked = _cfg.Get("pago_tarjeta_habilitada") != "false";
            ChkTarjetaComision.IsChecked = _cfg.Get("pago_tarjeta_comision_habilitada") == "true";
            TxtTarjetaComision.Text = string.IsNullOrWhiteSpace(_cfg.Get("pago_tarjeta_comision_pct")) ? "0.00" : _cfg.Get("pago_tarjeta_comision_pct");

            ChkTransferenciaHabilitada.IsChecked = _cfg.Get("pago_transferencia_habilitada") == "true";
            TxtTransferenciaAlias.Text = string.IsNullOrWhiteSpace(_cfg.Get("pago_transferencia_alias")) ? "Transferencia" : _cfg.Get("pago_transferencia_alias");

            ChkChequeHabilitado.IsChecked = _cfg.Get("pago_cheque_habilitado") == "true";
            ChkValesHabilitado.IsChecked = _cfg.Get("pago_vales_habilitado") == "true";
            ChkMixtoHabilitado.IsChecked = _cfg.Get("pago_mixto_habilitado") != "false";
        }

        private void BtnGuardar_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _cfg.Set("pago_efectivo_no_menor", ChkEfectivoNoMenor.IsChecked == true ? "true" : "false");
                _cfg.Set("pago_dolar_habilitado", ChkDolarHabilitado.IsChecked == true ? "true" : "false");
                _cfg.Set("pago_dolar_tipo_cambio", NormalizarDecimal(TxtTipoCambio.Text, 1m));

                _cfg.Set("pago_tarjeta_habilitada", ChkTarjetaHabilitada.IsChecked == true ? "true" : "false");
                _cfg.Set("pago_tarjeta_comision_habilitada", ChkTarjetaComision.IsChecked == true ? "true" : "false");
                _cfg.Set("pago_tarjeta_comision_pct", NormalizarDecimal(TxtTarjetaComision.Text, 0m));

                _cfg.Set("pago_transferencia_habilitada", ChkTransferenciaHabilitada.IsChecked == true ? "true" : "false");
                _cfg.Set("pago_transferencia_alias", string.IsNullOrWhiteSpace(TxtTransferenciaAlias.Text) ? "Transferencia" : TxtTransferenciaAlias.Text.Trim());

                _cfg.Set("pago_cheque_habilitado", ChkChequeHabilitado.IsChecked == true ? "true" : "false");
                _cfg.Set("pago_vales_habilitado", ChkValesHabilitado.IsChecked == true ? "true" : "false");
                _cfg.Set("pago_mixto_habilitado", ChkMixtoHabilitado.IsChecked == true ? "true" : "false");

                MessageBox.Show("Formas de pago guardadas.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo guardar:\n" + ex.Message);
            }
        }

        private static string NormalizarDecimal(string? texto, decimal porDefecto)
        {
            if (decimal.TryParse(texto ?? "", NumberStyles.Any, CultureInfo.InvariantCulture, out var iv))
                return iv.ToString(CultureInfo.InvariantCulture);
            if (decimal.TryParse(texto ?? "", NumberStyles.Any, CultureInfo.GetCultureInfo("es-CL"), out var cl))
                return cl.ToString(CultureInfo.InvariantCulture);
            return porDefecto.ToString(CultureInfo.InvariantCulture);
        }
    }
}