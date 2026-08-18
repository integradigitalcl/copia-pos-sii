using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GrunflexPOS2.Data;
using GrunflexPOS2.Models.Entities;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Discovery;
using GrunflexPOS2.Services.Licensing;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS2.Views;

public partial class ConectarMasCajasWindow : Window
{
    private string? _plantillaJson;
    private readonly ServerDiscoveryService _discovery = new();
    private readonly ObservableCollection<DiscoveredServer> _cajasPrincipales = new();
    private CancellationTokenSource? _ctsDescubrimiento;

    public ConectarMasCajasWindow()
    {
        InitializeComponent();
        ListaCajasPrincipales.ItemsSource = _cajasPrincipales;
        ChkUsarIpLan.Checked += ChkUsarIpLan_Changed;
        ChkUsarIpLan.Unchecked += ChkUsarIpLan_Changed;
        TxtManualServidor.TextChanged += TxtManualServidor_TextChanged;
        CmbEmpresa.SelectionChanged += (_, _) => ActualizarHintPasoCrear();
        Loaded += async (_, _) =>
        {
            CargarEmpresas();
            await BuscarCajasPrincipalesAsync();
        };
    }

    private void ChkUsarIpLan_Changed(object sender, RoutedEventArgs e)
    {
        ActualizarTextoIpPlantilla();
        ActualizarHintPasoCrear();
    }

    private void TxtManualServidor_TextChanged(object sender, TextChangedEventArgs e) => ActualizarHintPasoCrear();

    private async void BtnBuscarCajas_Click(object sender, RoutedEventArgs e) => await BuscarCajasPrincipalesAsync();

    private void ListaCajasPrincipales_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ActualizarTextoIpPlantilla();
        ActualizarHintPasoCrear();
    }

    private void ActualizarTextoIpPlantilla()
    {
        if (!IsLoaded)
            return;

        if (ChkUsarIpLan.IsChecked != true)
        {
            TxtIpPlantilla.Text =
                "Sin «Usar la IP de red»: el otro PC usará el nombre de equipo de Windows de este servidor para la base compartida (UNC).";
            return;
        }

        var host = ResolverHostParaPlantilla();
        if (!string.IsNullOrWhiteSpace(host))
        {
            TxtIpPlantilla.Text =
                $"El otro equipo se conectará al servidor de datos/API usando: {host} " +
                "(API, pago y ruta \\\\servidor\\GrunflexPOS según corresponda).";
        }
        else
        {
            TxtIpPlantilla.Text =
                "No hay aún host para la plantilla: escriba IP o nombre arriba, elija una fila de la lista o pulse «Volver a buscar».";
        }
    }

    private void ListaCajasPrincipales_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ListaCajasPrincipales.SelectedItem == null)
            return;
        if (BtnConectarCaja.IsEnabled)
            BtnConectarCaja.Focus();
        else
            BtnCrear.Focus();
        ActualizarHintPasoCrear();
    }

    private bool PuedeUsarBotonConectarCaja() =>
        BtnCrear.IsEnabled
        && (ChkUsarIpLan.IsChecked != true || !string.IsNullOrWhiteSpace(ResolverHostParaPlantilla()));

    private void ActualizarEstadoBotonConectarCaja() => BtnConectarCaja.IsEnabled = PuedeUsarBotonConectarCaja();

    private void ActualizarHintPasoCrear()
    {
        if (!IsLoaded)
            return;

        ActualizarEstadoBotonConectarCaja();
        if (!BtnCrear.IsEnabled)
        {
            TxtHintAccion.Text = string.Empty;
            return;
        }

        var hostOk = !string.IsNullOrWhiteSpace(ResolverHostParaPlantilla());

        TxtHintAccion.Text = PuedeUsarBotonConectarCaja()
            ? "«Conectar caja» registra la caja, genera la plantilla y la deja lista (Escritorio, portapapeles y se abre el Explorador en el archivo). Prioridad del servidor: campo manual si es válido; si no, fila seleccionada; si no, IP de red de esta PC."
            : ChkUsarIpLan.IsChecked == true && !hostOk
                ? "Active la IP de red o indique IP/nombre del servidor en el campo manual, o elija una fila en la lista."
                : "Use «Crear y preparar otro PC» si prefiere no usar «Conectar caja», o revise empresa y nombre de caja.";
    }

    private async Task BuscarCajasPrincipalesAsync()
    {
        _ctsDescubrimiento?.Cancel();
        _ctsDescubrimiento = new CancellationTokenSource();
        BtnBuscarCajas.IsEnabled = false;
        TxtDescubrimiento.Text = "Buscando cajas principales en la red…";
        TxtIpPlantilla.Text = string.Empty;
        TxtHintAccion.Text = string.Empty;
        _cajasPrincipales.Clear();
        try
        {
            var resultados = await _discovery.ScanAsync(TimeSpan.FromSeconds(3), _ctsDescubrimiento.Token);
            var locales = EnumerarIpsLocalesV4();
            foreach (var s in resultados
                         .Select(r => ConEtiquetaLan(r, locales))
                         .OrderBy(x => x.Ip, StringComparer.OrdinalIgnoreCase))
                _cajasPrincipales.Add(s);

            if (_cajasPrincipales.Count == 0)
            {
                var ipLocal = ObtenerIpv4LanPreferida();
                if (!string.IsNullOrWhiteSpace(ipLocal))
                {
                    _cajasPrincipales.Add(new DiscoveredServer
                    {
                        Server = $"Esta PC — servidor principal ({Environment.MachineName})",
                        Ip = ipLocal,
                        ApiPort = 7279,
                        ApiBaseUrl = $"http://{ipLocal}:7279/",
                        Version = "?"
                    });
                }

                TxtDescubrimiento.Text = string.IsNullOrWhiteSpace(ipLocal)
                    ? "No hubo respuestas en la red y no se detectó IP LAN en esta PC. Escriba la IP o nombre del servidor en el campo manual."
                    : "No hubo respuestas por broadcast (UDP). Se añadió «Esta PC» con su IP de red: suele ser el servidor donde corre la API. El equipo nuevo no aparece en esta lista hasta tener Grunflex instalado.";
            }
            else
            {
                TxtDescubrimiento.Text =
                    $"{_cajasPrincipales.Count} servidor(es) con API en la red. " +
                    "«Esta PC — …» es este equipo; «En red — …» es otro. El PC del terminal nuevo no se lista aquí: use el nombre de caja más abajo para identificarlo en el POS.";
                ListaCajasPrincipales.SelectedIndex = 0;
            }

            ActualizarTextoIpPlantilla();
            ActualizarHintPasoCrear();
        }
        catch (Exception ex)
        {
            TxtDescubrimiento.Text = "Error en la búsqueda: " + ex.Message;
            PosDiagnostics.Log("ConectarMasCajas discovery", ex);
        }
        finally
        {
            BtnBuscarCajas.IsEnabled = true;
            ActualizarHintPasoCrear();
        }
    }

    private void CargarEmpresas()
    {
        if (App.DbContext == null)
        {
            TxtEstado.Text = "No hay conexión a base de datos.";
            BtnProbar.IsEnabled = false;
            BtnCrear.IsEnabled = false;
            BtnConectarCaja.IsEnabled = false;
            TxtHintAccion.Text = string.Empty;
            return;
        }

        var empresas = App.DbContext.Empresas.OrderBy(e => e.Nombre).ToList();
        CmbEmpresa.ItemsSource = empresas;
        if (empresas.Count > 0)
            CmbEmpresa.SelectedIndex = 0;
        else
        {
            TxtEstado.Text = "No hay empresas registradas. Crea una empresa antes de agregar cajas.";
            BtnCrear.IsEnabled = false;
            BtnConectarCaja.IsEnabled = false;
            TxtHintAccion.Text = string.Empty;
            return;
        }

        var cs = App.DbContext.Database.GetConnectionString()?.Trim();
        if (!string.IsNullOrWhiteSpace(cs))
            TxtCadenaPrueba.Text = cs;

        TxtNombreCaja.Text = SugerirNombreCaja();

        var ip = ObtenerIpv4LanPreferida();
        if (ChkUsarIpLan.IsChecked == true && !string.IsNullOrEmpty(ip))
            TxtEstado.Text = $"Listo. IP detectada para la plantilla: {ip}. Pulse «Crear y preparar otro PC».";
        else if (ChkUsarIpLan.IsChecked == true)
            TxtEstado.Text = "No se detectó IP de red (solo probada conexión local). La plantilla usará los hosts actuales.";
        else
            TxtEstado.Text = "Listo. Revise el nombre y pulse «Crear y preparar otro PC».";

        try
        {
            if (App.DbContext.Database.CanConnect())
                TxtEstado.Text += " Conexión a base: OK.";
        }
        catch
        {
            TxtEstado.Text += " No se pudo verificar la conexión ahora.";
        }

        ActualizarTextoIpPlantilla();
        ActualizarHintPasoCrear();
    }

    private string SugerirNombreCaja()
    {
        if (App.DbContext == null)
            return "Caja nueva";

        var nombres = App.DbContext.Cajas.Select(c => c.Nombre ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var maxNum = 0;
        foreach (var nombre in nombres)
        {
            var m = Regex.Match(nombre, @"^Caja\s+(\d+)$", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var num))
                maxNum = Math.Max(maxNum, num);
        }

        for (var i = Math.Max(maxNum, 0) + 1; i < 10_000; i++)
        {
            var propuesto = $"Caja {i}";
            if (!nombres.Contains(propuesto))
                return propuesto;
        }

        return $"Caja {Guid.NewGuid():N}".Substring(0, 12);
    }

    private static string? ObtenerIpv4LanPreferida()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback)
                    continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(ua.Address))
                        continue;
                    var s = ua.Address.ToString();
                    if (s.StartsWith("169.254.", StringComparison.Ordinal))
                        continue;
                    return s;
                }
            }
        }
        catch
        {
            // ignorar
        }

        return null;
    }

    private string? ResolverHostParaPlantilla()
    {
        var man = TxtManualServidor?.Text?.Trim();
        if (!string.IsNullOrEmpty(man) && EsHostOIpValido(man))
            return man;
        if (ListaCajasPrincipales.SelectedItem is DiscoveredServer ds && !string.IsNullOrWhiteSpace(ds.Ip))
            return ds.Ip.Trim();
        return ObtenerIpv4LanPreferida();
    }

    private static bool EsHostOIpValido(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return false;
        s = s.Trim();
        if (s.Contains(' ', StringComparison.Ordinal) || s.Contains('\t', StringComparison.Ordinal))
            return false;
        if (IPAddress.TryParse(s, out _))
            return true;
        return Uri.CheckHostName(s) != UriHostNameType.Unknown;
    }

    private static HashSet<string> EnumerarIpsLocalesV4()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    set.Add(ua.Address.ToString());
                }
            }
        }
        catch
        {
            // ignorar
        }

        return set;
    }

    private static DiscoveredServer ConEtiquetaLan(DiscoveredServer s, HashSet<string> locales)
    {
        var hostDisp = string.IsNullOrWhiteSpace(s.Server) ? s.Ip : s.Server.Trim();
        var esLocal = locales.Contains(s.Ip);
        var etiqueta = esLocal
            ? $"Esta PC — servidor principal ({hostDisp})"
            : $"En red — caja principal ({hostDisp})";
        return new DiscoveredServer
        {
            Server = etiqueta,
            Ip = s.Ip,
            ApiPort = s.ApiPort,
            ApiBaseUrl = s.ApiBaseUrl,
            Version = s.Version,
            RequestId = s.RequestId
        };
    }

    private static void IntentarAbrirExploradorEnArchivo(string rutaArchivo)
    {
        if (string.IsNullOrWhiteSpace(rutaArchivo) || !File.Exists(rutaArchivo))
            return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{rutaArchivo}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // ignorar
        }
    }

    private static void IntentarEjecutarScriptElevado(string rutaPs1)
    {
        if (string.IsNullOrWhiteSpace(rutaPs1) || !File.Exists(rutaPs1))
            return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{rutaPs1}\"",
                Verb = "runas",
                UseShellExecute = true
            });
        }
        catch
        {
            // usuario canceló UAC o no hay permisos
        }
    }

    private static bool EsCadenaSqlite(string? cs)
    {
        if (string.IsNullOrWhiteSpace(cs))
            return false;
        var t = cs.Trim();
        return t.StartsWith("Data Source", StringComparison.OrdinalIgnoreCase)
               || t.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("Filename=", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtraerDataSourceSqlite(string cs)
    {
        if (string.IsNullOrWhiteSpace(cs))
            return string.Empty;

        var parts = cs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts)
        {
            var eq = p.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = p[..eq].Trim();
            if (!key.Equals("Data Source", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("Filename", StringComparison.OrdinalIgnoreCase))
                continue;
            return p[(eq + 1)..].Trim();
        }

        return string.Empty;
    }

    private static string BuildSharedSqliteConnectionString(string currentConnectionString, string serverHost)
    {
        // El share "GrunflexPOS" apunta directamente a la carpeta donde vive grunflex.db.
        // Por eso el path UNC NO debe incluir un subdirectorio \data\. (Antes había un bug
        // que generaba \\IP\GrunflexPOS\data\grunflex.db y fallaba con "carpeta no encontrada".)
        const string sharedTemplate = @"Data Source=\\{0}\GrunflexPOS\grunflex.db;Cache=Shared";

        var currentDataSource = ExtraerDataSourceSqlite(currentConnectionString);
        if (string.IsNullOrWhiteSpace(currentDataSource))
            return string.Format(sharedTemplate, serverHost);

        if (currentDataSource.StartsWith(@"\\", StringComparison.Ordinal))
            return currentConnectionString;

        return string.Format(sharedTemplate, serverHost);
    }

    private static string BuildShareScript(string localSqliteConnection)
    {
        var dataSource = ExtraerDataSourceSqlite(localSqliteConnection);
        var databaseFolder = string.IsNullOrWhiteSpace(dataSource)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GrunflexPOS", "data")
            : Path.GetDirectoryName(dataSource)
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GrunflexPOS", "data");

        return
            "$ErrorActionPreference = 'Stop'\n" +
            "$ConfirmPreference = 'None'\n" +
            "$ProgressPreference = 'SilentlyContinue'\n" +
            "$shareName = 'GrunflexPOS'\n" +
            $"$folder = '{databaseFolder.Replace("'", "''")}'\n" +
            "if (-not (Test-Path $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }\n" +
            "$existing = Get-SmbShare -Name $shareName -ErrorAction SilentlyContinue\n" +
            "if ($existing) {\n" +
            "  Set-SmbShare -Name $shareName -Path $folder -Force -ErrorAction SilentlyContinue | Out-Null\n" +
            "  Grant-SmbShareAccess -Name $shareName -AccountName 'Everyone' -AccessRight Change -Force -ErrorAction SilentlyContinue | Out-Null\n" +
            "} else {\n" +
            "  New-SmbShare -Name $shareName -Path $folder -ChangeAccess 'Everyone' -Force | Out-Null\n" +
            "}\n" +
            "try {\n" +
            "  $acl = Get-Acl $folder\n" +
            "  $sidUsers = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-545')\n" +
            "  $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($sidUsers,'Modify','ContainerInherit,ObjectInherit','None','Allow')\n" +
            "  $acl.SetAccessRule($rule)\n" +
            "  Set-Acl -Path $folder -AclObject $acl -Confirm:$false\n" +
            "} catch { Write-Warning ('NTFS (se omitio permiso local): ' + $_.Exception.Message) }\n" +
            "try {\n" +
            "  Set-NetFirewallRule -DisplayGroup 'Uso compartido de archivos e impresoras' -Enabled True -ErrorAction SilentlyContinue\n" +
            "  Set-NetFirewallRule -DisplayGroup 'File and Printer Sharing' -Enabled True -ErrorAction SilentlyContinue\n" +
            "} catch { }\n" +
            "try {\n" +
            "  $reglaApi = 'Grunflex POS API (7279)'\n" +
            "  if (Get-NetFirewallRule -DisplayName $reglaApi -ErrorAction SilentlyContinue) {\n" +
            "    Set-NetFirewallRule -DisplayName $reglaApi -Enabled True -Action Allow -Profile Any | Out-Null\n" +
            "  } else {\n" +
            "    New-NetFirewallRule -DisplayName $reglaApi -Direction Inbound -Protocol TCP -LocalPort 7279 -Action Allow -Profile Any | Out-Null\n" +
            "  }\n" +
            "} catch { Write-Warning ('FW: ' + $_.Exception.Message) }\n" +
            "Write-Host \"Recurso compartido listo: \\\\$env:COMPUTERNAME\\$shareName\" -ForegroundColor Green\n" +
            "Write-Host \"Regla firewall TCP 7279 (API) habilitada.\" -ForegroundColor Green\n";
    }

    private static string ReemplazarHostEnConnectionString(string cs, string nuevoHost)
    {
        if (string.IsNullOrWhiteSpace(cs) || string.IsNullOrWhiteSpace(nuevoHost))
            return cs;

        var parts = cs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq <= 0)
                continue;
            var key = parts[i][..eq].Trim();
            var val = parts[i][(eq + 1)..].Trim();
            if (!key.Equals("Host", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("Server", StringComparison.OrdinalIgnoreCase))
                continue;

            if (val.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                val.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                val.Equals("::1", StringComparison.OrdinalIgnoreCase))
                parts[i] = $"{key}={nuevoHost}";
        }

        return string.Join(';', parts);
    }

    private static string ReemplazarHostEnUrl(string url, string nuevoHost)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(nuevoHost))
            return url;
        try
        {
            var u = new Uri(url.Trim());
            var b = new UriBuilder(u) { Host = nuevoHost };
            return b.Uri.ToString();
        }
        catch
        {
            return url;
        }
    }

    private void BtnProbar_Click(object sender, RoutedEventArgs e)
    {
        TxtEstado.Text = string.Empty;
        var csPrueba = TxtCadenaPrueba.Text?.Trim();
        try
        {
            if (string.IsNullOrWhiteSpace(csPrueba))
            {
                if (App.DbContext == null)
                {
                    MessageBox.Show("No hay contexto de base de datos.", "Probar conexión", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!App.DbContext.Database.CanConnect())
                {
                    MessageBox.Show("No se pudo conectar con la configuración actual.", "Probar conexión", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            else
            {
                if (!EsCadenaSqlite(csPrueba))
                {
                    MessageBox.Show(
                        "Solo se admite probar conexiones SQLite (Data Source=ruta\\archivo.db).",
                        "Probar conexión",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var options = new DbContextOptionsBuilder<GrunflexDbContext>()
                    .UseSqlite(csPrueba)
                    .Options;
                using var db = new GrunflexDbContext(options);
                if (!db.Database.CanConnect())
                {
                    MessageBox.Show("No se pudo conectar con la cadena indicada.", "Probar conexión", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            TxtEstado.Text = "Conexión correcta.";
            MessageBox.Show("Conexión correcta.", "Probar conexión", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Error al probar conexión:\n" + ex.Message, "Probar conexión", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnConectarCaja_Click(object sender, RoutedEventArgs e)
    {
        ChkUsarIpLan.IsChecked = true;
        CrearYPrepararOtroPc(exigirCajaPrincipalEnLista: true, tituloExito: "Conectar caja");
    }

    private void BtnCrear_Click(object sender, RoutedEventArgs e) =>
        CrearYPrepararOtroPc(exigirCajaPrincipalEnLista: false, tituloExito: "Crear y preparar otro PC");

    private void CrearYPrepararOtroPc(bool exigirCajaPrincipalEnLista, string tituloExito)
    {
        TxtEstado.Text = string.Empty;
        if (App.DbContext == null)
        {
            MessageBox.Show("No hay conexión a base de datos.", "Crear caja", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (exigirCajaPrincipalEnLista && ChkUsarIpLan.IsChecked == true &&
            string.IsNullOrWhiteSpace(ResolverHostParaPlantilla()))
        {
            MessageBox.Show(
                "No se pudo determinar la IP o el nombre del servidor para la plantilla. " +
                "Escriba la IP o el nombre en el campo manual, elija una fila en la lista, pulse «Volver a buscar» " +
                "o desmarque «Usar la IP de red…» para usar solo el nombre de equipo de Windows.",
                tituloExito,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (CmbEmpresa.SelectedValue is not Guid empresaId)
        {
            MessageBox.Show("Seleccione una empresa.", "Crear caja", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var nombre = TxtNombreCaja.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(nombre))
        {
            MessageBox.Show("Ingrese el nombre de la caja.", "Crear caja", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (App.DbContext.Cajas.Any(c => c.Nombre == nombre))
        {
            MessageBox.Show("Ya existe una caja con ese nombre.", "Crear caja", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var cfg = new ConfiguracionService();
            if (!CajaSlotGate.CanCreateActiveCaja(App.DbContext, cfg, out var limiteMsg))
            {
                MessageBox.Show(limiteMsg, "Límite de cajas", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var nueva = new Caja
            {
                Id = Guid.NewGuid(),
                Nombre = nombre,
                EmpresaId = empresaId,
                Activa = true
            };
            App.DbContext.Cajas.Add(nueva);
            App.DbContext.SaveChanges();

            var usarIp = ChkUsarIpLan.IsChecked == true;
            var ipLan = usarIp ? ResolverHostParaPlantilla() : null;
            _plantillaJson = ConstruirPlantillaJson(nueva.Id, usarIp, ipLan);
            TxtPlantilla.Text = _plantillaJson;
            BtnCopiar.IsEnabled = true;
            TxtEstado.Text = $"Caja creada. CajaId: {nueva.Id}";

            string? rutaArchivo = null;
            string? rutaScriptShare = null;
            string? rutaTerminalDesktop = null;
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrWhiteSpace(desktop))
                    desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var nombreSeguro = string.Join("_", nombre.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
                if (string.IsNullOrWhiteSpace(nombreSeguro))
                    nombreSeguro = "caja";
                rutaArchivo = Path.Combine(desktop, $"grunflex-appsettings-{nombreSeguro}.json");
                File.WriteAllText(rutaArchivo, _plantillaJson);
                rutaTerminalDesktop = Path.Combine(desktop, "grunflex-terminal.json");
                File.WriteAllText(rutaTerminalDesktop, _plantillaJson);
                try
                {
                    var juntoExe = Path.Combine(AppContext.BaseDirectory, "grunflex-terminal.json");
                    File.WriteAllText(juntoExe, _plantillaJson);
                }
                catch
                {
                    // Program Files u otra ruta sin escritura: se ignora
                }

                rutaScriptShare = Path.Combine(desktop, "habilitar-recurso-grunflexpos.ps1");
                File.WriteAllText(rutaScriptShare, BuildShareScript(App.DbContext?.Database.GetConnectionString()?.Trim() ?? string.Empty));
            }
            catch (Exception exFile)
            {
                rutaArchivo = null;
                TxtEstado.Text += $" (No se pudo guardar en Escritorio: {exFile.Message})";
            }

            var clipOk = true;
            string? clipError = null;
            try
            {
                Clipboard.SetText(_plantillaJson);
            }
            catch (Exception exClip)
            {
                clipOk = false;
                clipError = exClip.Message;
            }

            if (rutaTerminalDesktop != null && File.Exists(rutaTerminalDesktop))
                IntentarAbrirExploradorEnArchivo(rutaTerminalDesktop);

            if (ChkEjecutarScriptRed.IsChecked == true && rutaScriptShare != null)
                IntentarEjecutarScriptElevado(rutaScriptShare);

            var estado = $"Caja «{nombre}» registrada (Id {nueva.Id}). ";
            if (rutaTerminalDesktop != null && File.Exists(rutaTerminalDesktop))
                estado += "Se abrió el Explorador en grunflex-terminal.json; la plantilla está en el portapapeles. ";
            else if (rutaArchivo != null)
                estado += $"Plantilla en {rutaArchivo}. ";
            if (ChkEjecutarScriptRed.IsChecked == true && rutaScriptShare != null)
                estado += "Si aceptó el aviso de administrador, se ejecutó el script de recurso compartido y firewall. ";
            if (!clipOk)
                estado += "No se pudo copiar al portapapeles: " + clipError;
            if (usarIp && string.IsNullOrEmpty(ipLan))
                estado += " Aviso: no hay host LAN; revise la cadena y las URLs de la API en la plantilla.";
            TxtEstado.Text = estado;

            TxtNombreCaja.Text = SugerirNombreCaja();
            ActualizarHintPasoCrear();
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo crear la caja:\n" + ex.Message, "Crear caja", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string ConstruirPlantillaJson(Guid cajaId, bool usarIpLan, string? ipLan)
    {
        var connectionString = App.DbContext?.Database.GetConnectionString()?.Trim() ?? string.Empty;
        string apiBase;
        string pagoBase;

        try
        {
            var cfg = AppConfig.Cargar();
            if (string.IsNullOrWhiteSpace(connectionString))
                connectionString = cfg.ConnectionString ?? string.Empty;
            apiBase = cfg.ApiBaseUrl?.Trim() ?? "http://127.0.0.1:7279/";
            pagoBase = cfg.PagoApiBaseUrl?.Trim() ?? "http://127.0.0.1:7279/api/pago";
        }
        catch
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                connectionString = "";
            apiBase = "http://127.0.0.1:7279/";
            pagoBase = "http://127.0.0.1:7279/api/pago";
        }

        if (EsCadenaSqlite(connectionString))
        {
            var serverHost = (usarIpLan && !string.IsNullOrWhiteSpace(ipLan))
                ? ipLan!
                : Environment.MachineName;
            connectionString = BuildSharedSqliteConnectionString(connectionString, serverHost);
        }

        if (usarIpLan && !string.IsNullOrWhiteSpace(ipLan))
        {
            apiBase = ReemplazarHostEnUrl(apiBase, ipLan);
            pagoBase = ReemplazarHostEnUrl(pagoBase, ipLan);
        }

        // Nota: la plantilla NO incluye flags de producto (Multicaja/SoporteOnline).
        // Esas funciones se habilitan únicamente con licencia firmada y nunca desde archivos JSON.
        var doc = new Dictionary<string, object?>
        {
            ["ConnectionStrings"] = new Dictionary<string, string?> { ["Default"] = connectionString },
            ["Api"] = new Dictionary<string, string?> { ["BaseUrl"] = apiBase, ["PagoBaseUrl"] = pagoBase },
            ["CajaId"] = cajaId.ToString(),
            ["TerminalRole"] = "client",
            ["Multicaja"] = new Dictionary<string, object?>
            {
                ["UseApiOnlyClient"] = true,
                ["CatalogSyncIntervalSeconds"] = 5,
                ["SharedSecret"] = ""
            },
            ["SmbShareUser"] = MulticajaLanDefaults.ShareUser,
            ["SmbSharePassword"] = MulticajaLanDefaults.SharePassword
        };

        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    private void BtnCopiar_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_plantillaJson) && !string.IsNullOrWhiteSpace(TxtPlantilla.Text))
            _plantillaJson = TxtPlantilla.Text;

        if (string.IsNullOrWhiteSpace(_plantillaJson))
        {
            MessageBox.Show("Primero cree una caja para generar la plantilla.", "Copiar", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Clipboard.SetText(_plantillaJson);
            TxtEstado.Text = "Plantilla copiada al portapapeles.";
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo copiar:\n" + ex.Message, "Copiar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
