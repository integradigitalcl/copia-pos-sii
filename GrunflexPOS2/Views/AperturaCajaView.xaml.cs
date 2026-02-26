using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Views
{
    public partial class AperturaCajaView : Window
    {
        private bool _formateando = false;

        public AperturaCajaView()
        {
            InitializeComponent();
        }

        private void Cerrar_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void AbrirCaja_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(NumeroCajaTextBox.Text, out int numeroCaja))
            {
                MessageBox.Show("Número de caja inválido.");
                return;
            }

            string cajero = CajeroTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(cajero))
            {
                MessageBox.Show("Debe ingresar nombre del cajero.");
                return;
            }

            string montoLimpio = MontoInicialTextBox.Text.Replace(".", "");

            if (!decimal.TryParse(montoLimpio, out decimal montoInicial))
            {
                MessageBox.Show("Monto inicial inválido.");
                return;
            }

            CajaService.AbrirCaja(numeroCaja, cajero, montoInicial);

            DialogResult = true;
            Close();
        }

        // Solo permite números
        private void MontoInicial_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !e.Text.All(char.IsDigit);
        }

        // Formatea automáticamente 10000 -> 10.000
        private void MontoInicial_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_formateando) return;

            _formateando = true;

            var textBox = sender as System.Windows.Controls.TextBox;
            string limpio = textBox.Text.Replace(".", "");

            if (decimal.TryParse(limpio, out decimal valor))
            {
                textBox.Text = string.Format(
                    CultureInfo.GetCultureInfo("es-CL"),
                    "{0:N0}",
                    valor);

                textBox.CaretIndex = textBox.Text.Length;
            }

            _formateando = false;
        }
    }
}