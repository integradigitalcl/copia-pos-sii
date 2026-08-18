using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GrunflexPOS2;
using GrunflexPOS2.Data;
using GrunflexPOS2.Models;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Multicaja;

namespace GrunflexPOS2.Views
{
    public partial class CorteView : UserControl
    {
        private readonly ResumenCorte _resumen;
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
            string mes = _cl.DateTimeFormat.GetMonthName(_resumen.FechaComercial.Month);
            mes = char.ToUpper(mes[0], _cl) + mes.Substring(1);
            TxtFecha.Text = $"{_resumen.FechaComercial.Day} de {mes} de {_resumen.FechaComercial:yyyy}";

            TxtVentasTotales.Text = _resumen.TotalVentas.ToString("C0", _cl);
            TxtEsperado.Text = _resumen.DineroEsperadoEnCaja.ToString("C0", _cl);
            TxtGanancia.Text = _resumen.TotalGanancia.ToString("C0", _cl);

            TxtMontoInicial.Text = _resumen.MontoInicial.ToString("C2", _cl);
            TxtEfectivo.Text = _resumen.TotalEfectivo.ToString("C2", _cl);
            TxtEntradas.Text = _resumen.TotalEntradas.ToString("C2", _cl);
            TxtSalidas.Text = _resumen.TotalSalidas.ToString("C2", _cl);
            TxtDevoluciones.Text = _resumen.TotalDevoluciones.ToString("C2", _cl);

            TxtDebito.Text = _resumen.TotalDebito.ToString("C2", _cl);
            TxtCredito.Text = _resumen.TotalCredito.ToString("C2", _cl);
            TxtTransferencia.Text = _resumen.TotalTransferencia.ToString("C2", _cl);
            TxtAnulaciones.Text = _resumen.TotalAnuladas.ToString("C2", _cl);

            TxtTotalEnCaja.Text = _resumen.DineroEsperadoEnCaja.ToString("C2", _cl);

            decimal totalMetodos = _resumen.TotalDebito + _resumen.TotalCredito + _resumen.TotalTransferencia +
                                   _resumen.TotalAnuladas;
            TxtTotalMetodos.Text = totalMetodos.ToString("C2", _cl);

            TxtTotalVentasRealizadas.Text = _resumen.TotalVentasRealizadas.ToString("N0", _cl);
            TxtTotalArticulos.Text = _resumen.TotalArticulosVendidos.ToString("N0", _cl);
            TxtConsumoMovimientos.Text = _resumen.MovimientosConsumoPersonal.ToString("N0", _cl);
            TxtConsumoUnidades.Text = _resumen.ArticulosConsumoPersonal.ToString("N0", _cl);
            TxtImpuestos.Text = _resumen.TotalImpuestos.ToString("C2", _cl);
            TxtGananciaNeta.Text = _resumen.TotalGanancia.ToString("C2", _cl);

            TxtDineroContado.Text = "0";
            ActualizarDiferenciaUi();
        }

        private void TxtDineroContado_TextChanged(object sender, TextChangedEventArgs e) =>
            ActualizarDiferenciaUi();

        private void ActualizarDiferenciaUi()
        {
            string texto = TxtDineroContado.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(texto))
            {
                TxtDiferencia.Text = "—";
                TxtDiferencia.Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128));
                return;
            }

            if (!decimal.TryParse(texto, NumberStyles.Any, _cl, out decimal contado))
            {
                TxtDiferencia.Text = "—";
                TxtDiferencia.Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128));
                return;
            }

            _resumen.DineroRealEnCaja = contado;
            TxtDiferencia.Text = _resumen.Diferencia.ToString("C2", _cl);

            if (_resumen.Diferencia < 0)
                TxtDiferencia.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            else
                TxtDiferencia.Foreground = new SolidColorBrush(Color.FromRgb(29, 78, 216));
        }

        private void BtnImprimirExportar_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.ContextMenu != null)
            {
                b.ContextMenu.PlacementTarget = b;
                b.ContextMenu.Placement = PlacementMode.Bottom;
                b.ContextMenu.IsOpen = true;
            }
        }

        private void ImprimirCorte_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var pd = new PrintDialog();
                if (pd.ShowDialog() == true)
                    pd.PrintVisual(this, "Corte de caja");
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo imprimir:\n" + ex.Message, "Imprimir", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ExportarCortePdf_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "La exportación a PDF del corte estará disponible en una próxima versión.",
                "Exportar PDF",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private async void CerrarCaja_Click(object sender, RoutedEventArgs e)
        {
            if (!decimal.TryParse(TxtDineroContado.Text, NumberStyles.Any, _cl, out decimal contado))
            {
                MessageBox.Show("Debe ingresar el dinero contado antes de cerrar el turno.");
                return;
            }

            _resumen.DineroRealEnCaja = contado;

            GuardarCorteHistorico(_resumen);

            if (MulticajaRuntime.UseApiOnlyClient)
            {
                var sesion = App.ObtenerSesionCajaAbiertaVisual();
                if (sesion == null || App.UsuarioActual == null)
                {
                    MessageBox.Show("No hay sesión de caja o usuario para cerrar en el servidor.");
                    return;
                }

                var body = new MulticajaCierreCajaRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    CajaId = sesion.CajaId,
                    CajaSesionId = sesion.Id,
                    UsuarioCierreId = App.UsuarioActual.Id,
                    MontoContado = contado
                };

                try
                {
                    var resp = await MulticajaOperacionesClient.CerrarSesionAsync(body).ConfigureAwait(true);
                    if (resp == null || !resp.Ok)
                    {
                        MessageBox.Show(resp?.Error ?? "No se pudo cerrar la caja en el servidor.");
                        return;
                    }

                    App.MulticajaSesionEnServidor = null;
                }
                catch (HttpRequestException)
                {
                    MulticajaOfflineEnqueue.TryEnqueue("multicaja-cierre-sesion", body, body.RequestId);
                    MessageBox.Show(
                        "Error de red. Si la cola offline está habilitada, el cierre quedó pendiente de envío.");
                    return;
                }
                catch (TaskCanceledException)
                {
                    MulticajaOfflineEnqueue.TryEnqueue("multicaja-cierre-sesion", body, body.RequestId);
                    MessageBox.Show(
                        "Timeout. Si la cola offline está habilitada, el cierre quedó pendiente de envío.");
                    return;
                }
            }
            else
            {
                var cajaService = new CajaService(App.DbContext);
                var sesion = cajaService.ObtenerSesionAbierta(App.CajaActualId);

                if (sesion != null)
                    cajaService.CerrarCaja(sesion.Id, contado);
            }

            MessageBox.Show("Caja cerrada correctamente.");

            var parent = Window.GetWindow(this) as CajaView;
            parent?.Close();
        }

        private void GuardarCorteHistorico(ResumenCorte resumen)
        {
            try
            {
                var cfg = AppConfig.Cargar();
                var ruta = string.IsNullOrWhiteSpace(cfg.CortesHistoricoLocalPath)
                    ? LocalDatabasePaths.DefaultCortesHistoricoLocalJsonPath
                    : cfg.CortesHistoricoLocalPath.Trim();

                var dir = Path.GetDirectoryName(ruta);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

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
                MessageBox.Show("Error al guardar el histórico del corte.");
            }
        }
    }
}