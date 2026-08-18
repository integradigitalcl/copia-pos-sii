using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GrunflexPOS2.Data;
using GrunflexPOS2.Infrastructure.Setup;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Discovery;

namespace GrunflexPOS2.Views;

public partial class ConectarServidorWindow : Window
{
    private readonly ServerDiscoveryService _discovery = new();
    private readonly ObservableCollection<DiscoveredServer> _servidores = new();
    private CancellationTokenSource? _cts;

    /// <summary>Si true, se abrió desde el instalador (caja adicional): textos y cierre sin pedir reinicio manual.</summary>
    public bool ModoInstalador { get; set; }

    public ConectarServidorWindow()
    {
        InitializeComponent();
        ListaServidores.ItemsSource = _servidores;
        Loaded += OnLoadedPrimeraVez;
    }

    private async void OnLoadedPrimeraVez(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedPrimeraVez;
        if (ModoInstalador)
            Title = "Conectar a la caja principal (instalación)";

        await EscanearAsync();
    }

    private async Task EscanearAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _servidores.Clear();
        BtnReescanear.IsEnabled = false;
        BtnConectar.IsEnabled = false;
        EstadoTxt.Text = "Buscando cajas principales en la red...";
        try
        {
            var resultados = await _discovery.ScanAsync(TimeSpan.FromSeconds(3), _cts.Token);
            foreach (var s in resultados) _servidores.Add(s);

            if (_servidores.Count == 0)
            {
                EstadoTxt.Text = "No se encontraron cajas principales. Verifica que estén encendidas o ingresa la IP manualmente.";
            }
            else
            {
                EstadoTxt.Text = $"{_servidores.Count} caja(s) encontrada(s). Seleccione una y presione Conectar.";
                ListaServidores.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            EstadoTxt.Text = "Error en la búsqueda: " + ex.Message;
            PosDiagnostics.Log("Discovery scan falló", ex);
        }
        finally
        {
            BtnReescanear.IsEnabled = true;
            BtnConectar.IsEnabled = true;
        }
    }

    private async void Reescanear_Click(object sender, RoutedEventArgs e) => await EscanearAsync();

    private async void Conectar_Click(object sender, RoutedEventArgs e)
    {
        // 1) si hay selección de la lista, usarla
        DiscoveredServer? elegido = ListaServidores.SelectedItem as DiscoveredServer;

        // 2) si hay IP manual, validar y construir un DiscoveredServer
        var ipManual = (IpManualTxt.Text ?? "").Trim();
        if (elegido == null && !string.IsNullOrWhiteSpace(ipManual))
        {
            if (!IPAddress.TryParse(ipManual, out _) && !ipManual.Any(char.IsLetter))
            {
                MessageBox.Show("La IP ingresada no es válida.", "Conectar a servidor",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            elegido = new DiscoveredServer
            {
                Server = ipManual,
                Ip = ipManual,
                ApiPort = 7279,
                ApiBaseUrl = $"http://{ipManual}:7279/",
                Version = "?"
            };
        }

        if (elegido == null)
        {
            MessageBox.Show("Seleccione una caja de la lista o ingrese la IP manualmente.",
                "Conectar a servidor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        BtnConectar.IsEnabled = false;
        EstadoTxt.Text = $"Probando conectividad con {elegido.Ip}...";
        try
        {
            var ok = await ProbarApiAsync(elegido.ApiBaseUrl).ConfigureAwait(true);
            if (!ok)
            {
                var continuar = MessageBox.Show(
                    $"No se pudo contactar la API en {elegido.ApiBaseUrl}.\n\n" +
                    "Esto puede deberse a firewall, servidor apagado o IP incorrecta.\n\n" +
                    "¿Aplicar la configuración igualmente?",
                    "Conectividad",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (continuar != MessageBoxResult.Yes)
                {
                    EstadoTxt.Text = "Conexión cancelada.";
                    return;
                }
            }

            var hostPlantilla = !string.IsNullOrWhiteSpace(elegido.Ip)
                ? elegido.Ip.Trim()
                : (elegido.Server ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(hostPlantilla))
            {
                MessageBox.Show(
                    "No se pudo determinar la dirección del servidor (IP o nombre).",
                    "Conectar a servidor",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            MulticajaInstallerConfigWriter.WriteApiOnlyClient(hostPlantilla);

            if (ModoInstalador)
            {
                MessageBox.Show(
                    $"Este equipo quedó configurado para usar la caja principal en {hostPlantilla}.\n\n" +
                    "Cierre esta ventana y pulse «Finalizar» en el instalador. Luego abra Grunflex POS desde el menú Inicio.",
                    "Instalación — multicaja",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    $"Configuración aplicada.\n\nServidor: {elegido.Server}\nIP: {elegido.Ip}\n\n" +
                    "Reinicie el POS para que tome los cambios.",
                    "Conectado",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo aplicar la configuración:\n" + ex.Message,
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnConectar.IsEnabled = true;
        }
    }

    private static async Task<bool> ProbarApiAsync(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var resp = await http.GetAsync(baseUrl.TrimEnd('/') + "/health/live");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private void Cancelar_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
