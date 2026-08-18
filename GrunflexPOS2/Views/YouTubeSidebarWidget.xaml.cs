using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace GrunflexPOS2.Views;

public partial class YouTubeSidebarWidget : UserControl
{
    private const string PlaylistId = "PL5vxSeWWyCs";
    private const string PortalUrl = "https://music.youtube.com/";
    private const string VirtualHost = "grunflex-youtube.local";

    private bool _playerListo;
    private bool _reproduciendo;

    public event EventHandler? OpenFullScreenRequested;

    public YouTubeSidebarWidget()
    {
        InitializeComponent();
        Loaded += YouTubeSidebarWidget_Loaded;
    }

    private async void YouTubeSidebarWidget_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= YouTubeSidebarWidget_Loaded;
        EstablecerControlesHabilitados(false);

        try
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GrunflexPOS", "webview", "youtube-sidebar");
            var hostDir = Path.Combine(baseDir, "host");
            Directory.CreateDirectory(hostDir);

            await File.WriteAllTextAsync(
                Path.Combine(hostDir, "player.html"),
                CrearPlayerHtml(),
                Encoding.UTF8).ConfigureAwait(true);

            var env = await CoreWebView2Environment.CreateAsync(null, baseDir).ConfigureAwait(true);
            await Player.EnsureCoreWebView2Async(env).ConfigureAwait(true);

            Player.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Player.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

            Player.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHost,
                hostDir,
                CoreWebView2HostResourceAccessKind.Allow);

            Player.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            Player.CoreWebView2.Navigate($"https://{VirtualHost}/player.html");
        }
        catch
        {
            EstablecerControlesHabilitados(false);
        }
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        var msg = args.TryGetWebMessageAsString();
        if (string.IsNullOrWhiteSpace(msg))
            return;

        Dispatcher.Invoke(() =>
        {
            if (msg == "ready")
            {
                _playerListo = true;
                EstablecerControlesHabilitados(true);
                return;
            }

            if (msg.StartsWith("state:", StringComparison.Ordinal))
            {
                var playing = msg is "state:1" or "state:3";
                _reproduciendo = playing;
                BtnPlayPause.Content = playing ? "\uE769" : "\uE768";
            }

            if (msg.StartsWith("muted:", StringComparison.Ordinal))
            {
                var muted = msg.EndsWith("1", StringComparison.Ordinal);
                BtnMute.Content = muted ? "\uE74F" : "\uE767";
                BtnMute.Foreground = muted
                    ? new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xFF, 0x00, 0x00))
                    : new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xAA, 0xAA, 0xAA));
            }
        });
    }

    private void EstablecerControlesHabilitados(bool enabled)
    {
        BtnPlayPause.IsEnabled = enabled;
        BtnPrevious.IsEnabled = enabled;
        BtnNext.IsEnabled = enabled;
        BtnMute.IsEnabled = enabled;
    }

    private static string CrearPlayerHtml()
    {
        var origin = $"https://{VirtualHost}";
        return $@"<!DOCTYPE html>
<html><head><meta charset=""utf-8"">
<script src=""https://www.youtube.com/iframe_api""></script>
</head><body style=""margin:0;background:#000"">
<div id=""yt""></div>
<script>
var p;
function post(msg) {{
  try {{ window.chrome.webview.postMessage(msg); }} catch(e) {{}}
}}
function onYouTubeIframeAPIReady() {{
  p = new YT.Player('yt', {{
    height: '200', width: '320',
    playerVars: {{
      listType: 'playlist',
      list: '{PlaylistId}',
      origin: '{origin}',
      enablejsapi: 1,
      playsinline: 1,
      rel: 0,
      controls: 0,
      modestbranding: 1
    }},
    events: {{
      onReady: function() {{ post('ready'); }},
      onStateChange: function(e) {{ post('state:' + e.data); }}
    }}
  }});
}}
window.grunflexTogglePlay = function() {{
  if (!p || !p.getPlayerState) return 'error';
  var s = p.getPlayerState();
  if (s === 1 || s === 3) {{ p.pauseVideo(); return 'paused'; }}
  p.playVideo();
  return 'playing';
}};
window.grunflexPause = function() {{
  if (!p) return;
  p.pauseVideo();
  p.stopVideo();
}};
window.grunflexNext = function() {{ try {{ p.nextVideo(); }} catch(e) {{}} }};
window.grunflexPrev = function() {{ try {{ p.previousVideo(); }} catch(e) {{}} }};
window.grunflexToggleMute = function() {{
  if (!p) return 'muted:0';
  if (p.isMuted()) p.unMute(); else p.mute();
  post('muted:' + (p.isMuted() ? '1' : '0'));
  return 'muted:' + (p.isMuted() ? '1' : '0');
}};
</script></body></html>";
    }

    private async void BtnPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (!_playerListo || Player.CoreWebView2 == null)
            return;

        if (_reproduciendo)
            await Player.CoreWebView2.ExecuteScriptAsync("grunflexPause();").ConfigureAwait(true);
        else
            await Player.CoreWebView2.ExecuteScriptAsync("grunflexTogglePlay();").ConfigureAwait(true);

        await SincronizarEstadoAsync().ConfigureAwait(true);
    }

    private async void BtnPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (!_playerListo || Player.CoreWebView2 == null) return;
        await Player.CoreWebView2.ExecuteScriptAsync("grunflexPrev();").ConfigureAwait(true);
    }

    private async void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        if (!_playerListo || Player.CoreWebView2 == null) return;
        await Player.CoreWebView2.ExecuteScriptAsync("grunflexNext();").ConfigureAwait(true);
    }

    private async void BtnMute_Click(object sender, RoutedEventArgs e)
    {
        if (!_playerListo || Player.CoreWebView2 == null) return;
        await Player.CoreWebView2.ExecuteScriptAsync("grunflexToggleMute();").ConfigureAwait(true);
    }

    private async Task SincronizarEstadoAsync()
    {
        if (Player.CoreWebView2 == null)
            return;

        var raw = await Player.CoreWebView2.ExecuteScriptAsync(
            "p && p.getPlayerState ? p.getPlayerState() : -1").ConfigureAwait(true);

        await Dispatcher.InvokeAsync(() =>
        {
            _reproduciendo = raw is "1" or "3";
            BtnPlayPause.Content = _reproduciendo ? "\uE769" : "\uE768";
        });
    }

    private void BtnAbrir_Click(object sender, RoutedEventArgs e) => AbrirUrl(PortalUrl);

    private void BtnPantallaCompleta_Click(object sender, RoutedEventArgs e) =>
        OpenFullScreenRequested?.Invoke(this, EventArgs.Empty);

    private static void AbrirUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo abrir YouTube Music:\n" + ex.Message, "YouTube",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
