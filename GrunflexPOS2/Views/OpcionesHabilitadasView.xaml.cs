using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Views
{
    public partial class OpcionesHabilitadasView : UserControl
    {
        private readonly ConfiguracionService _cfg = new();

        public OpcionesHabilitadasView()
        {
            InitializeComponent();
            Cargar();
        }

        private void Cargar()
        {
            ChkUsarInventario.IsChecked = _cfg.GetUsarInventario();
            ChkOfrecerCredito.IsChecked = _cfg.GetOfrecerCredito();
            ChkProductoComun.IsChecked = _cfg.GetVentaProductoComun();
            ChkCalcularMargen.IsChecked = _cfg.GetCalcularMargenAutomatico();
            ChkRedondeo.IsChecked = _cfg.GetRedondeoHabilitado();

            TxtMargen.Text = _cfg.GetMargenPorcentaje().ToString("0.##");
            TxtAviso.Text = _cfg.GetAvisoOcasional();
            TxtCadaVentas.Text = _cfg.GetCadaNVentas().ToString();

            SeleccionarCombo(CmbMetodoCosto, _cfg.GetMetodoCostoInventario());
            SeleccionarCombo(CmbRedondeo, _cfg.GetModoRedondeo());
        }

        private static void SeleccionarCombo(ComboBox combo, string valor)
        {
            foreach (var item in combo.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Content?.ToString(), valor, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        private static string TextoCombo(ComboBox combo) =>
            (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;

        private void Guardar_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _cfg.SetUsarInventario(ChkUsarInventario.IsChecked == true);
                _cfg.SetMetodoCostoInventario(TextoCombo(CmbMetodoCosto));
                _cfg.SetOfrecerCredito(ChkOfrecerCredito.IsChecked == true);
                _cfg.SetVentaProductoComun(ChkProductoComun.IsChecked == true);
                _cfg.SetCalcularMargenAutomatico(ChkCalcularMargen.IsChecked == true);
                _cfg.SetRedondeoHabilitado(ChkRedondeo.IsChecked == true);
                _cfg.SetModoRedondeo(TextoCombo(CmbRedondeo));

                if (!decimal.TryParse(TxtMargen.Text.Trim(), out var margen))
                    margen = 20m;
                _cfg.SetMargenPorcentaje(margen);

                if (!int.TryParse(TxtCadaVentas.Text.Trim(), out var cada))
                    cada = 0;
                _cfg.SetCadaNVentas(cada);
                _cfg.SetAvisoOcasional(TxtAviso.Text.Trim());

                MessageBox.Show("Opciones guardadas correctamente.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo guardar opciones:\n" + ex.Message);
            }
        }

        private void Restaurar_Click(object sender, RoutedEventArgs e) => Cargar();
    }
}
