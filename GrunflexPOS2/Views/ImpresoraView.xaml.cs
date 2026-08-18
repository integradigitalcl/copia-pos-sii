using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using GrunflexPOS2.Models;
using GrunflexPOS2.Models.Entities;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Views
{
    public partial class ImpresoraView : UserControl
    {
        private readonly ConfiguracionService _cfg = new();
        private bool _cargando;

        public ImpresoraView()
        {
            _cargando = true;
            InitializeComponent();
            CargarImpresoras();
            CargarConfiguracion();
            _cargando = false;
            RefrescarVistaPrevia();
        }

        private void CargarImpresoras()
        {
            var impresoras = PrinterSettings.InstalledPrinters.Cast<string>().OrderBy(x => x).ToList();
            CmbImpresora.ItemsSource = impresoras;
        }

        private void CargarConfiguracion()
        {
            _cargando = true;
            string imp = _cfg.Get("impresora_nombre");
            if (!string.IsNullOrWhiteSpace(imp))
                CmbImpresora.SelectedItem = imp;
            if (CmbImpresora.SelectedIndex < 0 && CmbImpresora.Items.Count > 0)
                CmbImpresora.SelectedIndex = 0;

            SeleccionarCombo(CmbFuente, _cfg.Get("ticket_fuente"), "Courier New");
            TxtTamano.Text = string.IsNullOrWhiteSpace(_cfg.Get("ticket_tamano")) ? "10" : _cfg.Get("ticket_tamano");
            TxtColumnas.Text = string.IsNullOrWhiteSpace(_cfg.Get("ticket_columnas")) ? "36" : _cfg.Get("ticket_columnas");
            SeleccionarCombo(CmbAnchoMm, _cfg.Get("ticket_ancho_mm"), "80");
            TxtLineasArriba.Text = string.IsNullOrWhiteSpace(_cfg.Get("ticket_lineas_arriba")) ? "0" : _cfg.Get("ticket_lineas_arriba");
            TxtLineasAbajo.Text = string.IsNullOrWhiteSpace(_cfg.Get("ticket_lineas_abajo")) ? "5" : _cfg.Get("ticket_lineas_abajo");

            ChkTotalesNormal.IsChecked = _cfg.Get("ticket_totales_normal") == "true";
            ChkNegrita.IsChecked = _cfg.Get("ticket_negrita") == "true";
            ChkPrecioUnitario.IsChecked = _cfg.Get("ticket_incluir_precio_unitario") == "true";
            ChkDescripcionExtendida.IsChecked = _cfg.Get("ticket_descripcion_extendida") == "true";
            ChkDatosCliente.IsChecked = _cfg.Get("ticket_datos_cliente") == "true";

            TxtPreviewTitulo.Text = ValorCfg("ticket_linea_1", "Nombre de mi Negocio");
            TxtPreviewSubtitulo.Text = ValorCfg("ticket_linea_2", "Direccion 123 Col. Colonia");
            TxtPreviewTelefono.Text = ValorCfg("ticket_linea_3", "(555) 123 4567");
            TxtPreviewRfc.Text = ValorCfg("ticket_linea_4", "RFC003128ZBI");
            TxtPreviewHeaderItems.Text = ValorCfg("ticket_linea_5", "Cant.  Descripcion               Importe");
            TxtPreviewGuion.Text = ValorCfg("ticket_linea_6", "----------------------------------------");
            TxtPreviewArticulos.Text = ValorCfg("ticket_linea_10", "         No. de Articulos: 4");
            TxtPreviewTotal.Text = ValorCfg("ticket_linea_11", "             Total: $33.00");
            TxtPreviewFooter.Text = ValorCfg("ticket_linea_12", "Gracias por su compra");
            TxtPreviewFooter2.Text = ValorCfg("ticket_linea_13", "www.eleventa.com");
            _cargando = false;
        }

        private string ValorCfg(string key, string valorDefecto)
        {
            var v = _cfg.Get(key);
            return string.IsNullOrWhiteSpace(v) ? valorDefecto : v;
        }

        private static void SeleccionarCombo(ComboBox combo, string valor, string porDefecto)
        {
            string objetivo = string.IsNullOrWhiteSpace(valor) ? porDefecto : valor;
            foreach (var it in combo.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(it.Content?.ToString(), objetivo, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = it;
                    return;
                }
            }
            if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        private static string TextoCombo(ComboBox combo) =>
            (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;

        private void CmbImpresora_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_cargando)
                return;

            string nombre = CmbImpresora.SelectedItem?.ToString() ?? string.Empty;
            if (nombre.Contains("58", StringComparison.OrdinalIgnoreCase))
            {
                TxtColumnas.Text = "32";
                SeleccionarCombo(CmbAnchoMm, "58", "58");
            }
            else if (nombre.Contains("80", StringComparison.OrdinalIgnoreCase))
            {
                TxtColumnas.Text = "48";
                SeleccionarCombo(CmbAnchoMm, "80", "80");
            }

            RefrescarVistaPrevia();
        }

        private void Config_Changed(object sender, RoutedEventArgs e)
        {
            if (_cargando)
                return;
            RefrescarVistaPrevia();
        }

        private void RefrescarVistaPrevia()
        {
            if (TxtTamano is null || TxtColumnas is null || CmbFuente is null ||
                TxtPreviewTitulo is null || TxtPreviewItem1 is null || TxtPreviewItem2 is null || TxtPreviewItem3 is null ||
                TxtPreviewTotal is null || ChkNegrita is null || ChkPrecioUnitario is null ||
                ChkDescripcionExtendida is null || ChkTotalesNormal is null || TxtLineasArriba is null || TxtLineasAbajo is null ||
                ImgPreviewLogo is null || TxtPreviewFooter is null || TxtPreviewFooter2 is null)
            {
                return;
            }

            int.TryParse(TxtTamano.Text, out int tam);
            if (tam <= 0) tam = 10;
            int.TryParse(TxtColumnas.Text, out int cols);
            if (cols <= 0) cols = 36;

            var fuente = TextoCombo(CmbFuente);
            bool negrita = ChkNegrita.IsChecked == true;
            bool precioUnit = ChkPrecioUnitario.IsChecked == true;
            bool descExt = ChkDescripcionExtendida.IsChecked == true;

            TxtPreviewTitulo.FontFamily = new System.Windows.Media.FontFamily(fuente);
            TxtPreviewTitulo.FontWeight = negrita ? FontWeights.Bold : FontWeights.Normal;
            TxtPreviewTitulo.FontSize = Math.Max(10, tam + 2);

            if (string.IsNullOrWhiteSpace(TxtPreviewItem1.Text))
                TxtPreviewItem1.Text = "1     Agua Ciel 600ml            $7.00";
            if (string.IsNullOrWhiteSpace(TxtPreviewItem2.Text))
                TxtPreviewItem2.Text = "1     Coca Cola Light            $8.00";
            if (string.IsNullOrWhiteSpace(TxtPreviewItem3.Text))
                TxtPreviewItem3.Text = "1Kg   Tomate                    $10.00";

            TxtPreviewItem1.Text = AjustarLinea(TxtPreviewItem1.Text, cols);
            TxtPreviewItem2.Text = AjustarLinea(TxtPreviewItem2.Text, cols);
            TxtPreviewItem3.Text = AjustarLinea(TxtPreviewItem3.Text, cols);
            TxtPreviewHeaderItems.Text = AjustarLinea(TxtPreviewHeaderItems.Text, cols);
            TxtPreviewGuion.Text = AjustarLinea(TxtPreviewGuion.Text, cols);
            TxtPreviewArticulos.Text = AjustarLinea(TxtPreviewArticulos.Text, cols);
            TxtPreviewTotal.Text = AjustarLinea(TxtPreviewTotal.Text, cols);
            TxtPreviewFooter.Text = AjustarLinea(TxtPreviewFooter.Text, cols);
            TxtPreviewFooter2.Text = AjustarLinea(TxtPreviewFooter2.Text, cols);
            TxtPreviewTotal.FontWeight = ChkTotalesNormal.IsChecked == true ? FontWeights.Normal : FontWeights.Bold;

            string logoPath = LogoHelper.ObtenerRutaLogoPreferida();
            if (!string.IsNullOrWhiteSpace(logoPath) && File.Exists(logoPath))
            {
                ImgPreviewLogo.Source = null;
                var bmp = LogoHelper.CargarBitmapSinCache(logoPath);
                if (bmp == null)
                {
                    try
                    {
                        bmp = new BitmapImage(new Uri(logoPath, UriKind.Absolute));
                    }
                    catch
                    {
                        bmp = null;
                    }
                }

                ImgPreviewLogo.Source = bmp;
                ImgPreviewLogo.Visibility = bmp != null ? Visibility.Visible : Visibility.Collapsed;
                ImgPreviewLogo.UpdateLayout();
            }
            else
            {
                ImgPreviewLogo.Source = null;
                ImgPreviewLogo.Visibility = Visibility.Collapsed;
            }
        }

        private static string AjustarLinea(string texto, int columnas)
        {
            if (string.IsNullOrEmpty(texto))
                return string.Empty;
            if (columnas < 15)
                columnas = 15;
            return texto.Length <= columnas ? texto : texto.Substring(0, columnas);
        }

        private void BtnGuardarConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _cfg.Set("impresora_nombre", CmbImpresora.SelectedItem?.ToString() ?? string.Empty);
                _cfg.Set("ticket_fuente", TextoCombo(CmbFuente));
                _cfg.Set("ticket_tamano", (int.TryParse(TxtTamano.Text, out int t) ? Math.Max(6, t) : 10).ToString());
                _cfg.Set("ticket_columnas", (int.TryParse(TxtColumnas.Text, out int c) ? Math.Max(20, c) : 36).ToString());
                _cfg.Set("ticket_ancho_mm", TextoCombo(CmbAnchoMm));
                _cfg.Set("ticket_lineas_arriba", (int.TryParse(TxtLineasArriba.Text, out int la) ? Math.Max(0, la) : 0).ToString());
                _cfg.Set("ticket_lineas_abajo", (int.TryParse(TxtLineasAbajo.Text, out int lb) ? Math.Max(0, lb) : 5).ToString());
                _cfg.Set("ticket_totales_normal", ChkTotalesNormal.IsChecked == true ? "true" : "false");
                _cfg.Set("ticket_negrita", ChkNegrita.IsChecked == true ? "true" : "false");
                _cfg.Set("ticket_incluir_precio_unitario", ChkPrecioUnitario.IsChecked == true ? "true" : "false");
                _cfg.Set("ticket_descripcion_extendida", ChkDescripcionExtendida.IsChecked == true ? "true" : "false");
                _cfg.Set("ticket_datos_cliente", ChkDatosCliente.IsChecked == true ? "true" : "false");
                _cfg.Set("ticket_linea_1", TxtPreviewTitulo.Text ?? "");
                _cfg.Set("ticket_linea_2", TxtPreviewSubtitulo.Text ?? "");
                _cfg.Set("ticket_linea_3", TxtPreviewTelefono.Text ?? "");
                _cfg.Set("ticket_linea_4", TxtPreviewRfc.Text ?? "");
                _cfg.Set("ticket_linea_5", TxtPreviewHeaderItems.Text ?? "");
                _cfg.Set("ticket_linea_6", TxtPreviewGuion.Text ?? "");
                _cfg.Set("ticket_linea_10", TxtPreviewArticulos.Text ?? "");
                _cfg.Set("ticket_linea_11", TxtPreviewTotal.Text ?? "");
                _cfg.Set("ticket_linea_12", TxtPreviewFooter.Text ?? "");
                _cfg.Set("ticket_linea_13", TxtPreviewFooter2.Text ?? "");

                MessageBox.Show("Configuración de impresión guardada.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo guardar configuración:\n" + ex.Message);
            }
        }

        private void BtnProbarImpresion_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BtnGuardarConfig_Click(sender, e);

                var demo = new Venta
                {
                    NumeroTicket = 999999,
                    Fecha = DateTime.Now,
                    Total = 5300,
                    Items = new List<DetalleVenta>
                    {
                        new DetalleVenta { Producto = "Agua Ciel 600ml", Cantidad = 1, Precio = 1000 },
                        new DetalleVenta { Producto = "Coca Cola Light", Cantidad = 2, Precio = 2150 }
                    }
                };

                TicketPdfService.GenerarTicketPDF(demo);
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo generar prueba de impresión:\n" + ex.Message);
            }
        }

        private void BtnAgregarLogo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Filter = "Imagen (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
                    Title = "Seleccionar logotipo"
                };

                if (dlg.ShowDialog() != true)
                    return;

                string destino = LogoHelper.ObtenerRutaLogoCustomUnica(".png");
                if (!LogoHelper.GuardarLogoNormalizado(dlg.FileName, destino))
                {
                    if (!LogoHelper.GuardarComoPngValido(dlg.FileName, destino))
                        LogoHelper.CopiarArchivoRobusto(dlg.FileName, destino);
                }
                LogoHelper.LimpiarLogosCustomExcept(destino);
                _cfg.Set("logo_path", destino);
                _cfg.Set("boleta_logo_path", destino);
                ImgPreviewLogo.Source = LogoHelper.CargarBitmapSinCache(destino);
                ImgPreviewLogo.Visibility = ImgPreviewLogo.Source != null ? Visibility.Visible : Visibility.Collapsed;
                RefrescarVistaPrevia();
                MessageBox.Show($"Logotipo actualizado correctamente.\nFormato recomendado: entre {LogoHelper.LogoAnchoMinPx} y {LogoHelper.LogoAnchoMaxPx}px de ancho, {LogoHelper.LogoAltoPx}px de alto.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo actualizar logotipo:\n" + ex.Message);
            }
        }

        private void BtnQuitarLogo_Click(object sender, RoutedEventArgs e)
        {
            _cfg.Set("logo_path", string.Empty);
            _cfg.Set("boleta_logo_path", string.Empty);
            LogoHelper.LimpiarLogosAntiguos();
            RefrescarVistaPrevia();
            MessageBox.Show("Logotipo quitado.");
        }
    }
}