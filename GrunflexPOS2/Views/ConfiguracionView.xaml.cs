using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GrunflexPOS2.Licensing;
using GrunflexPOS2.Security;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Views
{
    public partial class ConfiguracionView : UserControl
    {
        public event Action<UserControl>? OnNavigate;

        public ConfiguracionView()
        {
            InitializeComponent();
            Loaded += ConfiguracionView_Loaded;
            SizeChanged += ConfiguracionView_SizeChanged;
        }

        private void ConfiguracionView_SizeChanged(object sender, SizeChangedEventArgs e) =>
            AjustarTarjetasResponsive();

        private void AjustarTarjetasResponsive()
        {
            if (ActualWidth <= 0)
                return;

            var cardWidth = ActualWidth switch
            {
                >= 1100 => 340.0,
                >= 860 => 320.0,
                >= 620 => 300.0,
                >= 420 => Math.Max(280, (ActualWidth - 52) / 2),
                _ => Math.Max(260, ActualWidth - 36)
            };

            foreach (var panel in new[] { WrapGeneral, WrapPersonalizacion })
            {
                if (panel == null)
                    continue;

                foreach (var child in panel.Children)
                {
                    if (child is FrameworkElement fe)
                        fe.Width = cardWidth;
                }
            }

        }

        private void ConfiguracionView_Loaded(object sender, RoutedEventArgs e)
        {
            App.LicenseState.RefreshFromStores();
            var lic = App.LicenseState;

            BtnCardCaja.Visibility = lic.Multicaja ? Visibility.Visible : Visibility.Collapsed;

            AplicarPermisosAdminCards();
            AjustarTarjetasResponsive();
        }

        private void AplicarPermisosAdminCards()
        {
            var esAdmin = UsuarioPermisos.PuedeAccederConfiguracion();
            BtnCardLicencia.Visibility = esAdmin ? Visibility.Visible : Visibility.Collapsed;

            foreach (var tag in new[] { "Opciones", "Cajero", "BaseDatos", "Diagnostico" })
            {
                if (FindCardByTag(tag) is UIElement card)
                    card.Visibility = esAdmin ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private UIElement? FindCardByTag(string tag)
        {
            foreach (var panel in new[] { WrapGeneral, WrapPersonalizacion })
            {
                if (panel == null)
                    continue;

                foreach (var child in panel.Children)
                {
                    if (child is FrameworkElement fe &&
                        string.Equals(fe.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                        return fe;
                }
            }

            return null;
        }

        private void LicenciaEstado_Click(object sender, RoutedEventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureLicencias())
                return;

            OnNavigate?.Invoke(new LicenciaView());
        }

        private void Card_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag != null)
                NavigateFromTag(border.Tag.ToString());
        }

        private void CardButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag != null)
                NavigateFromTag(fe.Tag.ToString());
        }

        private void NavigateFromTag(string? rawTag)
        {
            if (string.IsNullOrWhiteSpace(rawTag))
                return;

            string opcion = rawTag.Trim().ToLowerInvariant();

            if (opcion is "cajero")
            {
                if (!UsuarioPermisosGate.EnsureUsuarios())
                    return;
                OnNavigate?.Invoke(new CajerosView());
                return;
            }

            if (opcion is "basedatos")
            {
                if (!UsuarioPermisosGate.EnsureBaseDatos())
                    return;
                OnNavigate?.Invoke(new BaseDatosView());
                return;
            }

            if (!UsuarioPermisosGate.EnsureConfiguracion())
                return;

            switch (opcion)
            {
                case "opciones":
                case "opciones habilitadas":
                    OnNavigate?.Invoke(new OpcionesHabilitadasView());
                    break;

                case "facturacion":
                    OnNavigate?.Invoke(new FacturacionView());
                    break;

                case "folio":
                    OnNavigate?.Invoke(new FolioView());
                    break;

                case "caja":
                    if (!App.LicenseState.Multicaja)
                    {
                        MessageBox.Show(
                            LicenseAccessGate.MensajeMulticajaRequerida,
                            "Multicaja",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return;
                    }
                    OnNavigate?.Invoke(new CajasView());
                    break;

                case "logo":
                    OnNavigate?.Invoke(new LogoView());
                    break;

                case "impresora":
                    OnNavigate?.Invoke(new ImpresoraView());
                    break;

                case "pagos":
                    OnNavigate?.Invoke(new FormasPagoView());
                    break;

                case "corte":
                    OnNavigate?.Invoke(new CorteConfigView());
                    break;

                case "unidades":
                    OnNavigate?.Invoke(new UnidadesView());
                    break;

                case "lector":
                case "lector de codigo":
                case "lector de codigos":
                case "scanner":
                    OnNavigate?.Invoke(new LectorCodigoView());
                    break;

                case "cajon":
                case "cajon de dinero":
                    OnNavigate?.Invoke(new CajonDineroView());
                    break;

                case "bascula":
                case "báscula":
                    OnNavigate?.Invoke(new BasculaView());
                    break;

                case "servipag":
                    OnNavigate?.Invoke(new NavegadorView("https://portal.servipag.com/"));
                    break;

                case "youtube":
                    OnNavigate?.Invoke(new YouTubeView());
                    break;

                case "correo":
                case "email":
                case "notificaciones":
                    OnNavigate?.Invoke(new CorreoView());
                    break;

                case "diagnostico":
                case "diagnóstico":
                    new DiagnosticoView { Owner = Window.GetWindow(this) }.ShowDialog();
                    break;

                default:
                    MessageBox.Show($"Módulo: {opcion}");
                    break;
            }
        }
    }
}