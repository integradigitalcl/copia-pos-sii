using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrunflexPOS2.Models;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Views
{
    public partial class CorteView : UserControl
    {
        private readonly ResumenCorte _resumen;

        // 🔥 Cultura fija Chile
        private readonly CultureInfo _cl = new CultureInfo("es-CL");

        public CorteView(ResumenCorte resumen)
        {
            InitializeComponent();
            _resumen = resumen;
            CargarDatos();
        }

        private void CargarDatos()
        {
            TxtCaja.Text = $"Caja: {_resumen.NumeroCaja}";
            TxtCajero.Text = $"Cajero: {_resumen.Cajero}";
            TxtFecha.Text = _resumen.FechaComercial.ToString("dd/MM/yyyy");

            // 🔹 Métricas principales
            TxtVentasTotales.Text = _resumen.TotalVentas.ToString("C0", _cl);
            TxtEsperado.Text = _resumen.DineroEsperadoEnCaja.ToString("C0", _cl);
            TxtGanancia.Text = _resumen.TotalGanancia.ToString("C0", _cl);

            // 🔹 Dinero en caja
            TxtMontoInicial.Text = _resumen.MontoInicial.ToString("C0", _cl);
            TxtEfectivo.Text = _resumen.TotalEfectivo.ToString("C0", _cl);
            TxtEntradas.Text = _resumen.TotalEntradas.ToString("C0", _cl);
            TxtSalidas.Text = _resumen.TotalSalidas.ToString("C0", _cl);
            TxtDevoluciones.Text = _resumen.TotalDevoluciones.ToString("C0", _cl);

            // 🔹 Ventas por método
            TxtDebito.Text = _resumen.TotalDebito.ToString("C0", _cl);
            TxtCredito.Text = _resumen.TotalCredito.ToString("C0", _cl);
            TxtTransferencia.Text = _resumen.TotalTransferencia.ToString("C0", _cl);
            TxtAnulaciones.Text = _resumen.TotalAnuladas.ToString("C0", _cl);

            // 🔹 Resumen operativo
            TxtTotalVentasRealizadas.Text = _resumen.TotalVentasRealizadas.ToString("N0", _cl);
            TxtTotalArticulos.Text = _resumen.TotalArticulosVendidos.ToString("N0", _cl);
            TxtImpuestos.Text = _resumen.TotalImpuestos.ToString("C0", _cl);

            TxtDiferencia.Text = "";
        }

        private void TxtDineroContado_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (decimal.TryParse(TxtDineroContado.Text, NumberStyles.Any, _cl, out decimal contado))
            {
                _resumen.DineroRealEnCaja = contado;

                TxtDiferencia.Text = _resumen.Diferencia.ToString("C0", _cl);

                if (_resumen.Diferencia < 0)
                    TxtDiferencia.Foreground = Brushes.Red;
                else if (_resumen.Diferencia > 0)
                    TxtDiferencia.Foreground = Brushes.Green;
                else
                    TxtDiferencia.Foreground = Brushes.Black;
            }
            else
            {
                TxtDiferencia.Text = "";
            }
        }

        private void CerrarCaja_Click(object sender, RoutedEventArgs e)
        {
            if (!decimal.TryParse(TxtDineroContado.Text, NumberStyles.Any, _cl, out decimal contado))
            {
                MessageBox.Show(
                    "Debe ingresar el dinero contado antes de cerrar el turno.",
                    "Validación requerida",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _resumen.DineroRealEnCaja = contado;

            GuardarCorteHistorico(_resumen);

            CajaService.CerrarCaja();

            MessageBox.Show(
                "Caja cerrada correctamente.",
                "Corte realizado",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            var parent = Window.GetWindow(this) as CajaView;
            parent?.Close();
        }

        private void GuardarCorteHistorico(ResumenCorte resumen)
        {
            try
            {
                string ruta = @"\\DESKTOP-7VI49G5\GrunflexPOS\cortes.json";

                List<ResumenCorte> cortes = new();

                if (File.Exists(ruta))
                {
                    var jsonExistente = File.ReadAllText(ruta);
                    cortes = JsonSerializer.Deserialize<List<ResumenCorte>>(jsonExistente)
                             ?? new List<ResumenCorte>();
                }

                cortes.Add(resumen);

                var json = JsonSerializer.Serialize(
                    cortes,
                    new JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(ruta, json);
            }
            catch
            {
                MessageBox.Show(
                    "Error al guardar el histórico del corte.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}