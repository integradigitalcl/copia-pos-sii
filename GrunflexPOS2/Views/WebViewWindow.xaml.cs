using System;
using System.Windows;
using System.Windows.Input;

namespace GrunflexPOS2.Views
{
    public partial class WebViewWindow : Window
    {
        private readonly string _urlInicial;

        public WebViewWindow(string url)
        {
            InitializeComponent();
            _urlInicial = url;

            Loaded += WebViewWindow_Loaded;
        }

        private async void WebViewWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await WebBrowser.EnsureCoreWebView2Async(null);

            WebBrowser.Source = new Uri(_urlInicial);
            TxtUrl.Text = _urlInicial;

            WebBrowser.SourceChanged += (s, args) =>
            {
                if (WebBrowser.Source != null)
                    TxtUrl.Text = WebBrowser.Source.ToString();
            };
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (WebBrowser.CanGoBack)
                WebBrowser.GoBack();
        }

        private void Forward_Click(object sender, RoutedEventArgs e)
        {
            if (WebBrowser.CanGoForward)
                WebBrowser.GoForward();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            WebBrowser.Reload();
        }

        private void TxtUrl_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                Navegar();
        }

        private void Navegar()
        {
            try
            {
                string url = TxtUrl.Text.Trim();

                if (!url.StartsWith("http"))
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
