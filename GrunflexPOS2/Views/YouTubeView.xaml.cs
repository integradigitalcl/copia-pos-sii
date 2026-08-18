using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace GrunflexPOS2.Views;

/// <summary>Portal YouTube Music por cliente (mismo patrón que Servipag).</summary>
public partial class YouTubeView : UserControl
{
    private const string PortalUrl = "https://music.youtube.com/";

    private bool _webViewListo;

    public YouTubeView()
    {
        InitializeComponent();
        Loaded += YouTubeView_Loaded;
    }

    private async void YouTubeView_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= YouTubeView_Loaded;

        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GrunflexPOS", "webview", "youtube");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(null, userData).ConfigureAwait(true);
            await Browser.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            Browser.CoreWebView2.Settings.IsZoomControlEnabled = true;
            Browser.CoreWebView2.Navigate(PortalUrl);
            _webViewListo = true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MostrarFalloWebView2();
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo abrir YouTube Music:\n" + ex.Message, "YouTube",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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

    private void MostrarFalloWebView2()
    {
        Browser.Visibility = Visibility.Collapsed;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "No está instalado WebView2. Abra YouTube Music en el navegador del sistema.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        var abrir = new Button
        {
            Content = "Abrir YouTube Music",
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        abrir.Click += (_, _) => AbrirExterno(PortalUrl);
        panel.Children.Add(abrir);

        var host = (Grid)Content;
        host.Children.Add(panel);
        Panel.SetZIndex(panel, 10);
    }

    private static void AbrirExterno(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* noop */ }
    }
}
