using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace GrunflexPOS2.Views
{
    public partial class WebViewWindow : Window
    {
        private readonly string _urlInicial;
        private bool _webViewListo;

        public WebViewWindow(string url)
        {
            InitializeComponent();
            _urlInicial = url;

            Loaded += WebViewWindow_Loaded;
        }

        private async void WebViewWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= WebViewWindow_Loaded;

            try
            {
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GrunflexPOS",
                    "WebView");

                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);

                await WebBrowser.EnsureCoreWebView2Async(env);

                WebBrowser.CoreWebView2.Settings.IsZoomControlEnabled = false;
                WebBrowser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                WebBrowser.CoreWebView2.Settings.AreDevToolsEnabled = false;

                WebBrowser.Source = new Uri(_urlInicial);
                TxtUrl.Text = _urlInicial;
                _webViewListo = true;

                WebBrowser.SourceChanged += (_, _) =>
                {
                    if (WebBrowser.Source != null)
                        TxtUrl.Text = WebBrowser.Source.ToString();
                };
            }
            catch (WebView2RuntimeNotFoundException)
            {
                if (MessageBox.Show(
                        "No está instalado Microsoft WebView2 Runtime en este equipo.\n\n¿Abrir la página en el navegador predeterminado?",
                        "Navegador",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(_urlInicial) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Navegador", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "No se pudo abrir el visor web integrado.\n\n" + ex.Message,
                    "Navegador",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Close();
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_webViewListo && WebBrowser.CanGoBack)
                WebBrowser.GoBack();
        }

        private void Forward_Click(object sender, RoutedEventArgs e)
        {
            if (_webViewListo && WebBrowser.CanGoForward)
                WebBrowser.GoForward();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (_webViewListo)
                WebBrowser.Reload();
        }

        private void TxtUrl_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                Navegar();
        }

        private void Navegar()
        {
            if (!_webViewListo || WebBrowser.CoreWebView2 == null)
                return;

            try
            {
                string url = TxtUrl.Text.Trim();

                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    url = "https://" + url;

                WebBrowser.Source = new Uri(url);
            }
            catch
            {
                MessageBox.Show("URL inválida.");
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
