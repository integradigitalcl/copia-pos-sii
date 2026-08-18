using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
namespace GrunflexPOS2.Views
{
    public partial class NavegadorView : UserControl
    {
        private readonly string _urlInicial;
        private bool _webViewListo;

        public NavegadorView(string url)
        {
            InitializeComponent();
            _urlInicial = url ?? "about:blank";

            Loaded += NavegadorView_Loaded;
        }

        private async void NavegadorView_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= NavegadorView_Loaded;
            TxtUrl.Text = _urlInicial;

            try
            {
                await Browser.EnsureCoreWebView2Async(null);
                Browser.CoreWebView2.Settings.IsZoomControlEnabled = true;
                Browser.CoreWebView2.Navigate(_urlInicial);
                _webViewListo = true;
            }
            catch (WebView2RuntimeNotFoundException)
            {
                MostrarFalloWebView2(
                    "No está instalado el runtime de Microsoft WebView2 en este equipo.\n\n" +
                    "Instálelo desde el sitio de Microsoft o abra el enlace en su navegador.");
            }
            catch (DllNotFoundException ex)
            {
                MostrarFalloWebView2(
                    "No se pudo cargar el componente WebView2.\n\n" + ex.Message);
            }
            catch (Exception ex)
            {
                MostrarFalloWebView2(
                    "No se pudo iniciar el navegador integrado.\n\n" + ex.Message);
            }
        }

        private void MostrarFalloWebView2(string mensaje)
        {
            _webViewListo = false;
            Browser.Visibility = Visibility.Collapsed;

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = mensaje,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            var abrir = new Button
            {
                Content = "Abrir en el navegador predeterminado",
                Padding = new Thickness(12, 8, 12, 8),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            abrir.Click += (_, _) => AbrirExterno(_urlInicial);
            panel.Children.Add(abrir);

            var descarga = new Button
            {
                Content = "Página de descarga WebView2 (Microsoft)",
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(12, 8, 12, 8),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            descarga.Click += (_, _) => AbrirExterno("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
            panel.Children.Add(descarga);

            var host = (Grid)Content;
            host.Children.Add(panel);
            Panel.SetZIndex(panel, 10);
        }

        private static void AbrirExterno(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo abrir el enlace:\n" + ex.Message, "Navegador", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Go_Click(object sender, RoutedEventArgs e)
        {
            if (!_webViewListo || Browser.CoreWebView2 == null)
            {
                AbrirExterno(TxtUrl.Text?.Trim() ?? _urlInicial);
                return;
            }

            Browser.CoreWebView2.Navigate(TxtUrl.Text);
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_webViewListo && Browser.CanGoBack)
                Browser.GoBack();
        }

        private void Forward_Click(object sender, RoutedEventArgs e)
        {
            if (_webViewListo && Browser.CanGoForward)
                Browser.GoForward();
        }

        private void Reload_Click(object sender, RoutedEventArgs e)
        {
            if (_webViewListo)
                Browser.Reload();
        }
    }
}
