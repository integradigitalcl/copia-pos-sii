using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Views
{
    public partial class LogoView : UserControl
    {
        private readonly ConfiguracionService _cfg = new();
        private string _rutaSeleccionada = string.Empty;

        public LogoView()
        {
            InitializeComponent();
            CargarLogoActual();
        }

        private void CargarLogoActual()
        {
            var ruta = LogoHelper.ObtenerRutaLogoPreferida();
            if (string.IsNullOrWhiteSpace(ruta) || !File.Exists(ruta))
            {
                _rutaSeleccionada = string.Empty;
                TxtRuta.Text = string.Empty;
                ImgPreview.Source = null;
                return;
            }

            _rutaSeleccionada = ruta;
            TxtRuta.Text = ruta;
            CargarPreview(ruta);
        }

        private void BtnBuscar_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Imagen (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
                Title = "Seleccionar logotipo"
            };

            if (dlg.ShowDialog() != true)
                return;

            _rutaSeleccionada = dlg.FileName;
            TxtRuta.Text = _rutaSeleccionada;
            CargarPreview(_rutaSeleccionada);
            if (ImgPreview.Source == null)
            {
                MessageBox.Show("No se pudo previsualizar la imagen seleccionada. Pruebe con PNG o JPG estándar.");
            }
        }

        private void BtnGuardar_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_rutaSeleccionada) || !File.Exists(_rutaSeleccionada))
                {
                    MessageBox.Show("Seleccione una imagen válida.");
                    return;
                }

                string destino = LogoHelper.ObtenerRutaLogoCustomUnica(".png");

                if (!LogoHelper.GuardarLogoNormalizado(_rutaSeleccionada, destino))
                {
                    if (!LogoHelper.GuardarComoPngValido(_rutaSeleccionada, destino))
                    {
                        // Fallback final: copiar binario tal cual si no se pudo recodificar.
                        LogoHelper.CopiarArchivoRobusto(_rutaSeleccionada, destino);
                    }
                }

                LogoHelper.LimpiarLogosCustomExcept(destino);

                _cfg.Set("logo_path", destino);
                _cfg.Set("boleta_logo_path", destino);
                _rutaSeleccionada = destino;
                TxtRuta.Text = destino;
                CargarPreview(destino);
                RefrescarCajaSiCorresponde();
                MessageBox.Show($"Logotipo actualizado correctamente.\nFormato recomendado: entre {LogoHelper.LogoAnchoMinPx} y {LogoHelper.LogoAnchoMaxPx}px de ancho, {LogoHelper.LogoAltoPx}px de alto.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo guardar el logotipo:\n" + ex.Message);
            }
        }

        private void BtnQuitar_Click(object sender, RoutedEventArgs e)
        {
            _cfg.Set("logo_path", string.Empty);
            _cfg.Set("boleta_logo_path", string.Empty);
            try
            {
                var rutaCustom = LogoHelper.ObtenerRutaLogoCustom();
                if (File.Exists(rutaCustom))
                    File.Delete(rutaCustom);
            }
            catch { }
            LogoHelper.LimpiarLogosAntiguos();
            _rutaSeleccionada = string.Empty;
            TxtRuta.Text = string.Empty;
            ImgPreview.Source = null;
            RefrescarCajaSiCorresponde();
            MessageBox.Show("Logotipo restaurado al texto por defecto.");
        }

        private void CargarPreview(string ruta)
        {
            try
            {
                ImgPreview.Source = null;
                var bmp = LogoHelper.CargarBitmapSinCache(ruta);
                if (bmp == null && !string.IsNullOrWhiteSpace(_rutaSeleccionada))
                {
                    // Si falla al leer destino, mostrar al menos la selección original.
                    bmp = LogoHelper.CargarBitmapSinCache(_rutaSeleccionada);
                }
                ImgPreview.Source = bmp;
                ImgPreview.UpdateLayout();
            }
            catch (Exception ex)
            {
                ImgPreview.Source = null;
                MessageBox.Show("Error al cargar vista previa del logo:\n" + ex.Message);
            }
        }

        private void RefrescarCajaSiCorresponde()
        {
            if (Window.GetWindow(this) is CajaView caja)
                caja.RefrescarLogoSuperior();
        }
    }
}