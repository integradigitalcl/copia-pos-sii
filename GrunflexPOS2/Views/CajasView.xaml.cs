using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GrunflexPOS2.Data;
using GrunflexPOS2.Licensing;
using GrunflexPOS2.Models.Entities;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Connectivity;
using GrunflexPOS2.Services.Multicaja;

namespace GrunflexPOS2.Views
{
    public partial class CajasView : UserControl
    {
        private List<Caja> _cajas = new();
        private Caja? _seleccionada;
        private DispatcherTimer? _timerEstadoConexion;
        private bool _timerEstadoConexionCorriendo;

        private static readonly JsonSerializerOptions JsonTerminals = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private sealed class TerminalListJson
        {
            [JsonPropertyName("machineName")]
            public string? MachineName { get; set; }

            [JsonPropertyName("ipAddress")]
            public string? IpAddress { get; set; }

            [JsonPropertyName("lastHeartbeatUtc")]
            public DateTime LastHeartbeatUtc { get; set; }

            [JsonPropertyName("active")]
            public bool Active { get; set; }
        }

        private sealed class CajaListaVm
        {
            public Caja? Caja { get; init; }
            public string NombreMostrar { get; init; } = "";
        }

        public CajasView()
        {
            InitializeComponent();
            Loaded += CajasView_Loaded;
            Unloaded += CajasView_Unloaded;
            _ = CargarCajasAsync();
            AplicarRolUI();
            AplicarPanelEstadoConexion();
        }

        private void CajasView_Loaded(object sender, RoutedEventArgs e) => IniciarTimerEstadoConexion();

        private void CajasView_Unloaded(object sender, RoutedEventArgs e) => DetenerTimerEstadoConexion();

        private void AplicarPanelEstadoConexion()
        {
            try
            {
                var cfg = AppConfig.Cargar();
                if (cfg.EsCajaPrincipal)
                {
                    PanelEstadoCliente.Visibility = Visibility.Collapsed;
                    PanelEstadoServidor.Visibility = Visibility.Visible;
                }
                else
                {
                    PanelEstadoCliente.Visibility = Visibility.Visible;
                    PanelEstadoServidor.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                PanelEstadoCliente.Visibility = Visibility.Visible;
                PanelEstadoServidor.Visibility = Visibility.Collapsed;
            }
        }

        private void IniciarTimerEstadoConexion()
        {
            if (_timerEstadoConexionCorriendo)
                return;
            _timerEstadoConexionCorriendo = true;
            _timerEstadoConexion = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _timerEstadoConexion.Tick += EstadoConexionTimer_Tick;
            _timerEstadoConexion.Start();
            _ = ActualizarEstadoConexionTickAsync();
        }

        private void DetenerTimerEstadoConexion()
        {
            _timerEstadoConexionCorriendo = false;
            if (_timerEstadoConexion == null)
                return;
            _timerEstadoConexion.Stop();
            _timerEstadoConexion.Tick -= EstadoConexionTimer_Tick;
            _timerEstadoConexion = null;
        }

        private void EstadoConexionTimer_Tick(object? sender, EventArgs e) =>
            _ = ActualizarEstadoConexionTickAsync();

        private async Task ActualizarEstadoConexionTickAsync()
        {
            try
            {
                var cfg = AppConfig.Cargar();
                if (cfg.EsCajaPrincipal)
                {
                    await ActualizarListaTerminalesServidorAsync(cfg).ConfigureAwait(true);
                    await CargarCajasAsync().ConfigureAwait(true);
                }
                else
                    ActualizarTextoCliente(cfg);
            }
            catch (Exception ex)
            {
                try
                {
                    PosDiagnostics.Log("CajasView.ActualizarEstadoConexionTickAsync", ex);
                }
                catch { /* */ }
            }
        }

        private void ActualizarTextoCliente(AppConfig cfg)
        {
            var api = (cfg.ApiBaseUrl ?? string.Empty).Trim();
            string hostApi = "(sin URL de API)";
            try
            {
                if (Uri.TryCreate(api, UriKind.Absolute, out var u))
                    hostApi = u.Host;
            }
            catch { /* */ }

            var cs = cfg.ConnectionString ?? string.Empty;
            string hostUnc = string.Empty;
            if (cs.Contains(@"\\", StringComparison.Ordinal))
            {
                var host = AppConfig.ExtraerHostUnc(cs);
                if (!string.IsNullOrWhiteSpace(host))
                    hostUnc = host;
            }

            var rol = MulticajaRuntime.UseApiOnlyClient ? "API-only (BD sombra local)" : "Multicaja (recurso de red)";
            var state = App.Connectivity?.State ?? ConnectivityState.Unknown;
            var lat = App.Connectivity?.LastLatencyMs ?? 0;
            var err = App.Connectivity?.LastError;
            var ultimoChequeoUtc = App.Connectivity?.LastCheckUtc ?? DateTime.MinValue;
            var estadoRed = state switch
            {
                ConnectivityState.Online => $"En línea · latencia ~{lat} ms",
                ConnectivityState.Degraded => $"Lenta / degradada · ~{lat} ms",
                ConnectivityState.Offline => "Sin conexión con la API",
                _ => "Comprobando…"
            };
            if (!string.IsNullOrWhiteSpace(err) && state != ConnectivityState.Online)
                estadoRed += $"\nDetalle: {err}";

            var destino = !string.IsNullOrWhiteSpace(hostUnc)
                ? $"Servidor (recurso SMB): \\\\{hostUnc}\\…"
                : $"Servidor (API): {api}\n   → Equipo / host: {hostApi}";

            var cajaTxt = string.IsNullOrWhiteSpace(cfg.CajaId) ? "(sin CajaId)" : cfg.CajaId.Trim();

            TxtEstadoCliente.Text =
                $"Este equipo: {Environment.MachineName}\n" +
                $"Modo: {rol}\n" +
                $"{destino}\n" +
                $"Caja asignada (Id): {cajaTxt}\n" +
                $"Estado hacia la API: {estadoRed}\n" +
                (ultimoChequeoUtc != DateTime.MinValue
                    ? $"Último chequeo: {ultimoChequeoUtc.ToLocalTime():HH:mm:ss}"
                    : "Último chequeo: —");
        }

        private async Task ActualizarListaTerminalesServidorAsync(AppConfig cfg)
        {
            var api = (cfg.ApiBaseUrl ?? "http://127.0.0.1:7279/").Trim();
            if (!api.EndsWith('/')) api += "/";
            var url = api + "api/terminals";

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                using var resp = await http.GetAsync(url).ConfigureAwait(true);
                if (!resp.IsSuccessStatusCode)
                {
                    ListaTerminales.ItemsSource = Array.Empty<string>();
                    TxtEstadoServidorResumen.Text =
                        $"No se pudo leer el registro de terminales (HTTP {(int)resp.StatusCode}).\n" +
                        $"URL probada: {url}\n" +
                        "Compruebe que el servicio GrunflexPOSAPI esté en ejecución en esta PC.";
                    return;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(true);
                var list = await JsonSerializer.DeserializeAsync<List<TerminalListJson>>(stream, JsonTerminals)
                    .ConfigureAwait(true) ?? new List<TerminalListJson>();

                var ahora = DateTime.UtcNow;
                var umbral = TimeSpan.FromMinutes(10);
                static bool EsLatidoLocal(TerminalListJson t) =>
                    string.Equals(t.MachineName?.Trim(), Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.IpAddress?.Trim(), "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.IpAddress?.Trim(), "::1", StringComparison.OrdinalIgnoreCase);

                var lineas = list
                    .OrderByDescending(t => t.LastHeartbeatUtc)
                    .Select(t =>
                    {
                        var nombre = string.IsNullOrWhiteSpace(t.MachineName) ? "(sin nombre)" : t.MachineName.Trim();
                        var ipRaw = t.IpAddress?.Trim() ?? "";
                        var esLocal = EsLatidoLocal(t);
                        var ip = string.IsNullOrWhiteSpace(ipRaw)
                            ? ""
                            : esLocal
                                ? " · (servicio API en esta PC, no es una caja adicional)"
                                : $" · IP {ipRaw}";
                        var delta = ahora - t.LastHeartbeatUtc;
                        var vivo = t.Active && delta <= umbral;
                        var estado = vivo ? "En línea" : (t.Active ? $"Último latido hace {FormatDuracion(delta)}" : "Inactiva / liberada");
                        var hbLocal = t.LastHeartbeatUtc.ToLocalTime();
                        return $"{nombre}{ip}\n   {estado} · último latido {hbLocal:dd/MM HH:mm:ss}";
                    })
                    .ToList();

                ListaTerminales.ItemsSource = lineas;
                var remotos = list.Where(t => !EsLatidoLocal(t)).ToList();
                var enLineaRemotos = remotos.Count(t => t.Active && (ahora - t.LastHeartbeatUtc) <= umbral);
                TxtEstadoServidorResumen.Text =
                    $"API local: {api}\n" +
                    $"Cajas adicionales en línea (≤10 min): {enLineaRemotos} de {remotos.Count} registradas en red.\n" +
                    (remotos.Count == 0
                        ? "Aún no hay latido desde otra PC: en la caja adicional inicie sesión en el POS (y verifique la IP del servidor).\n"
                        : "") +
                    $"Actualizado: {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                ListaTerminales.ItemsSource = Array.Empty<string>();
                TxtEstadoServidorResumen.Text =
                    "No se pudo consultar la lista de equipos conectados:\n" + ex.Message + "\n" +
                    "URL: " + url;
            }
        }

        private static string FormatDuracion(TimeSpan d)
        {
            if (d.TotalMinutes < 1) return $"{(int)d.TotalSeconds}s";
            if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min";
            if (d.TotalDays < 2) return $"{(int)d.TotalHours} h";
            return $"{(int)d.TotalDays} días";
        }

        /// <summary>
        /// La operación "Conectar más cajas" SOLO debe estar disponible desde la caja
        /// principal. Si esta PC es una caja adicional, ocultamos ese botón para evitar
        /// confusión sobre desde dónde sale la conexión. Análogamente, "Conectar a caja
        /// principal" no tiene sentido en el servidor: ya lo es.
        /// </summary>
        private void AplicarRolUI()
        {
            try
            {
                App.LicenseState.RefreshFromStores();
                if (!App.LicenseState.Multicaja)
                {
                    BtnConectarMasCajas.Visibility = Visibility.Collapsed;
                    BtnConectarServidor.Visibility = Visibility.Collapsed;
                    return;
                }

                var cfg = AppConfig.Cargar();
                if (cfg.EsCajaAdicional)
                {
                    BtnConectarMasCajas.Visibility = Visibility.Collapsed;
                    BtnConectarServidor.Visibility = Visibility.Visible;
                }
                else
                {
                    BtnConectarMasCajas.Visibility = Visibility.Visible;
                    BtnConectarServidor.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                BtnConectarMasCajas.Visibility = Visibility.Collapsed;
                BtnConectarServidor.Visibility = Visibility.Collapsed;
            }
        }

        private async Task CargarCajasAsync()
        {
            if (App.DbContext == null)
                return;

            _cajas = App.DbContext.Cajas
                .OrderBy(c => c.Nombre)
                .ToList();

            var cfg = AppConfig.Cargar();
            List<TerminalListJson>? terminales = null;
            if (cfg.EsCajaPrincipal)
                terminales = await ObtenerTerminalesApiAsync(cfg).ConfigureAwait(true);

            var vms = ConstruirListaCajasConEquipos(_cajas, terminales);
            var selId = _seleccionada?.Id;

            ListaCajas.ItemsSource = vms;
            if (selId != null)
            {
                var idx = vms.FindIndex(v => v.Caja?.Id == selId);
                ListaCajas.SelectedIndex = idx >= 0 ? idx : 0;
            }
            else if (vms.Count > 0)
                ListaCajas.SelectedIndex = 0;
            else
                ActualizarPanel(null);
        }

        private static List<CajaListaVm> ConstruirListaCajasConEquipos(
            List<Caja> cajas,
            List<TerminalListJson>? terminales)
        {
            var umbral = TimeSpan.FromMinutes(10);
            var ahora = DateTime.UtcNow;
            var remotos = (terminales ?? new List<TerminalListJson>())
                .Where(t => !EsTerminalLocal(t))
                .ToList();

            var usados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var resultado = new List<CajaListaVm>();

            foreach (var caja in cajas)
            {
                var match = EncontrarTerminalParaCaja(caja, remotos, usados);
                if (match != null)
                    usados.Add(match.MachineName!.Trim());

                resultado.Add(new CajaListaVm
                {
                    Caja = caja,
                    NombreMostrar = FormatearNombreCajaEnLista(caja, match, ahora, umbral)
                });
            }

            foreach (var t in remotos.Where(t => !usados.Contains(t.MachineName?.Trim() ?? "")))
            {
                var enLinea = t.Active && (ahora - t.LastHeartbeatUtc) <= umbral;
                var ip = string.IsNullOrWhiteSpace(t.IpAddress) ? "" : $" · {t.IpAddress.Trim()}";
                var estado = enLinea ? "en línea" : "sin latido reciente";
                resultado.Add(new CajaListaVm
                {
                    Caja = null,
                    NombreMostrar =
                        $"↳ {t.MachineName}{ip} — {estado} (sin caja asignada en lista)"
                });
            }

            return resultado;
        }

        private static TerminalListJson? EncontrarTerminalParaCaja(
            Caja caja,
            List<TerminalListJson> remotos,
            HashSet<string> yaUsados)
        {
            foreach (var t in remotos)
            {
                var mn = t.MachineName?.Trim() ?? "";
                if (string.IsNullOrEmpty(mn) || yaUsados.Contains(mn))
                    continue;
                if (caja.Nombre.Contains($"({mn})", StringComparison.OrdinalIgnoreCase)
                    || caja.Nombre.Contains(mn, StringComparison.OrdinalIgnoreCase))
                    return t;
            }

            return null;
        }

        private static string FormatearNombreCajaEnLista(
            Caja caja,
            TerminalListJson? terminal,
            DateTime ahoraUtc,
            TimeSpan umbral)
        {
            if (terminal == null)
            {
                if (caja.Nombre.Contains('('))
                    return $"{caja.Nombre} — sin latido reciente";
                return $"{caja.Nombre} — sin equipo conectado";
            }

            var enLinea = terminal.Active && (ahoraUtc - terminal.LastHeartbeatUtc) <= umbral;
            var mn = terminal.MachineName?.Trim() ?? "";
            var baseNombre = caja.Nombre.Trim();
            if (!baseNombre.Contains($"({mn})", StringComparison.OrdinalIgnoreCase))
                baseNombre = $"{baseNombre} ({mn})";

            var ip = string.IsNullOrWhiteSpace(terminal.IpAddress) ? "" : $" · {terminal.IpAddress.Trim()}";
            var estado = enLinea ? "● en línea" : "○ desconectada";
            return $"{baseNombre}{ip} — {estado}";
        }

        private static bool EsTerminalLocal(TerminalListJson t) =>
            string.Equals(t.MachineName?.Trim(), Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.IpAddress?.Trim(), "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.IpAddress?.Trim(), "::1", StringComparison.OrdinalIgnoreCase);

        private static async Task<List<TerminalListJson>?> ObtenerTerminalesApiAsync(AppConfig cfg)
        {
            var api = (cfg.ApiBaseUrl ?? "http://127.0.0.1:7279/").Trim();
            if (!api.EndsWith('/')) api += "/";
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                using var resp = await http.GetAsync(api + "api/terminals").ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return null;
                await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<List<TerminalListJson>>(stream, JsonTerminals)
                    .ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        private void ListaCajas_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _seleccionada = (ListaCajas.SelectedItem as CajaListaVm)?.Caja;
            ActualizarPanel(_seleccionada);
        }

        private void ActualizarPanel(Caja? caja)
        {
            BtnCambiarNombre.IsEnabled = caja != null;
            BtnEliminarCaja.IsEnabled = caja != null;

            if (caja == null || App.DbContext == null)
            {
                TxtNombreCaja.Text = "Seleccione una caja";
                TxtUltimoAcceso.Text = "Último acceso el: --";
                return;
            }

            var ultimaSesion = App.DbContext.CajaSesiones
                .Where(s => s.CajaId == caja.Id)
                .OrderByDescending(s => s.FechaApertura)
                .FirstOrDefault();

            TxtNombreCaja.Text = caja.Nombre;
            TxtUltimoAcceso.Text = ultimaSesion == null
                ? "Último acceso el: sin registros"
                : $"Último acceso el: {ultimaSesion.FechaApertura.ToLocalTime().ToString("dd/MMM/yyyy", new CultureInfo("es-CL"))}";
        }

        private void BtnCambiarNombre_Click(object sender, RoutedEventArgs e)
        {
            if (_seleccionada == null || App.DbContext == null)
                return;

            string nombreActual = _seleccionada.Nombre ?? string.Empty;
            string nuevo = Microsoft.VisualBasic.Interaction.InputBox(
                "Nuevo nombre de la caja:",
                "Cambiar nombre",
                nombreActual).Trim();

            if (string.IsNullOrWhiteSpace(nuevo) || string.Equals(nuevo, nombreActual, StringComparison.OrdinalIgnoreCase))
                return;

            bool existe = App.DbContext.Cajas.Any(c => c.Id != _seleccionada.Id && c.Nombre == nuevo);
            if (existe)
            {
                MessageBox.Show("Ya existe una caja con ese nombre.");
                return;
            }

            var edit = App.DbContext.Cajas.First(c => c.Id == _seleccionada.Id);
            edit.Nombre = nuevo;
            App.DbContext.SaveChanges();
            _ = CargarCajasAsync();
        }

        private void BtnEliminarCaja_Click(object sender, RoutedEventArgs e)
        {
            if (_seleccionada == null || App.DbContext == null)
                return;

            bool abierta = App.DbContext.CajaSesiones.Any(s => s.CajaId == _seleccionada.Id && s.Abierta);
            if (abierta)
            {
                MessageBox.Show("No se puede eliminar una caja con sesión abierta.");
                return;
            }

            if (App.DbContext.Cajas.Count() <= 1)
            {
                MessageBox.Show("Debe existir al menos una caja.");
                return;
            }

            var confirm = MessageBox.Show(
                $"¿Eliminar caja '{_seleccionada.Nombre}'?",
                "Confirmar",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;

            var sesiones = App.DbContext.CajaSesiones.Where(s => s.CajaId == _seleccionada.Id).ToList();
            var movimientos = App.DbContext.MovimientosCaja
                .Where(m => sesiones.Select(s => s.Id).Contains(m.CajaSesionId))
                .ToList();

            if (movimientos.Count > 0) App.DbContext.MovimientosCaja.RemoveRange(movimientos);
            if (sesiones.Count > 0) App.DbContext.CajaSesiones.RemoveRange(sesiones);
            var caja = App.DbContext.Cajas.First(c => c.Id == _seleccionada.Id);
            App.DbContext.Cajas.Remove(caja);
            App.DbContext.SaveChanges();

            _ = CargarCajasAsync();
        }

        private void BtnConectarMasCajas_Click(object sender, RoutedEventArgs e)
        {
            // Guard de rol: las cajas adicionales no pueden enrolar más cajas; ese flujo
            // sólo vive en la caja principal. Sin este chequeo, un operador podría intentar
            // configurar una "tercera caja" desde una segunda caja y todo se rompe porque
            // la API y la base central viven en la principal, no acá.
            var cfgRol = AppConfig.Cargar();
            if (cfgRol.EsCajaAdicional)
            {
                MessageBox.Show(
                    "Esta operación solo se puede iniciar desde la caja PRINCIPAL (la que aloja la base central " +
                    "y la API). Esta PC está configurada como caja adicional.\n\n" +
                    "Andá al servidor y desde ahí abrí 'Administrar cajas' → 'Conectar más cajas'.",
                    "Conexión multicaja",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            App.LicenseState.RefreshFromStores();
            if (!App.LicenseState.Multicaja)
            {
                MessageBox.Show(
                    LicenseAccessGate.MensajeMulticajaRequerida,
                    "Multicaja",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var owner = Window.GetWindow(this);
            var w = new ConectarMasCajasWindow { Owner = owner };
            w.ShowDialog();
            _ = CargarCajasAsync();
        }

        private void BtnConectarServidor_Click(object sender, RoutedEventArgs e)
        {
            App.LicenseState.RefreshFromStores();
            if (!App.LicenseState.Multicaja)
            {
                MessageBox.Show(
                    LicenseAccessGate.MensajeMulticajaRequerida,
                    "Multicaja",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var owner = Window.GetWindow(this);
            var w = new ConectarServidorWindow { Owner = owner };
            w.ShowDialog();
        }

        /// <summary>
        /// Aplica los cambios de cadena de conexión / endpoint multicaja sin que el
        /// operador tenga que cerrar el POS a mano. Internamente: valida la nueva
        /// configuración, prueba la conectividad a la BD y a la API, y relanza el
        /// proceso. Es más confiable que un "soft reload" porque el POS mantiene
        /// singletons (VentaService, ProductoLookup, TerminalRegistration) atados al
        /// DbContext anterior; cambiar ese DbContext en caliente puede dejar referencias
        /// huérfanas. El relaunch toma menos de 2 segundos y es transparente.
        /// </summary>
        private async void BtnActualizarConexion_Click(object sender, RoutedEventArgs e)
        {
            DeshabilitarBotones(true);
            PanelProgresoConexion.Visibility = Visibility.Visible;
            BarraProgresoConexion.Value = 0;
            BarraProgresoConexion.IsIndeterminate = false;

            try
            {
                var progreso = new Progress<(int valor, string paso)>(p =>
                {
                    BarraProgresoConexion.Value = p.valor;
                    TxtProgresoPaso.Text = p.paso;
                });

                bool ok = await EjecutarActualizacionConexionAsync(progreso);

                if (!ok)
                {
                    PanelProgresoConexion.Visibility = Visibility.Collapsed;
                    DeshabilitarBotones(false);
                    return;
                }

                ((IProgress<(int, string)>)progreso).Report((100, "Reiniciando POS..."));
                await Task.Delay(700);

                RelanzarPos();
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log("CajasView.BtnActualizarConexion_Click", ex);
                PanelProgresoConexion.Visibility = Visibility.Collapsed;
                DeshabilitarBotones(false);
                MessageBox.Show(
                    "No se pudo actualizar la conexión:\n\n" + ex.Message,
                    "Grunflex POS",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async Task<bool> EjecutarActualizacionConexionAsync(IProgress<(int, string)> progreso)
        {
            progreso.Report((5, "Releyendo y corrigiendo configuración local..."));
            await Task.Delay(150);

            // Cargar dispara auto-saneo (localhost → IP del servidor, rutas BD, etc.).
            var cfg = AppConfig.Cargar();
            cfg = AppConfig.Cargar();
            string cs = cfg.ConnectionString ?? string.Empty;
            string apiBase = cfg.ApiBaseUrl ?? string.Empty;

            if (string.IsNullOrWhiteSpace(cs) && !MulticajaRuntime.UseApiOnlyClient)
            {
                MessageBox.Show(
                    "La configuración local está vacía. Conectá primero a la caja principal " +
                    "(botón 'Conectar a caja principal') o registrá esta caja.",
                    "Grunflex POS",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            progreso.Report((20, "Validando acceso a la base de datos..."));
            await Task.Delay(150);

            string? dataSource = ExtraerDataSourceSqlite(cs);
            if (!MulticajaRuntime.UseApiOnlyClient &&
                !string.IsNullOrWhiteSpace(dataSource) &&
                !dataSource.Contains(@"\\", StringComparison.Ordinal) &&
                !File.Exists(dataSource))
            {
                MessageBox.Show(
                    "No se pudo abrir la base de datos configurada:\n\n" +
                    dataSource + "\n\n" +
                    "Verificá que el servidor esté encendido y compartiendo el recurso \\\\servidor\\GrunflexPOS, " +
                    "y que esta PC pueda alcanzarlo.",
                    "Grunflex POS",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (cfg.EsCajaPrincipal)
            {
                progreso.Report((40, "Verificando servicio GrunflexPOSAPI en esta PC..."));
                var apiOk = await ProbarApiAsync(apiBase).ConfigureAwait(true);
                if (!apiOk)
                {
                    progreso.Report((50, "Reiniciando servicio GrunflexPOSAPI..."));
                    var msg = ReiniciarServicioApi();
                    PosDiagnostics.Log("CajasView.actualizar-conexion: " + msg);
                    await Task.Delay(1200);
                    apiOk = await ProbarApiAsync(apiBase).ConfigureAwait(true);
                }

                if (!apiOk)
                {
                    var seguir = MessageBox.Show(
                        "La API local no respondió en el puerto 7279.\n\n" +
                        "Las cajas adicionales no podrán sincronizar inventario hasta que el servicio esté activo.\n\n" +
                        "¿Reiniciar el POS de todos modos?",
                        "Grunflex POS",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (seguir != MessageBoxResult.Yes)
                        return false;
                }
            }

            bool esMulticajaCliente = MulticajaRuntime.UseApiOnlyClient
                                      || cs.Contains(@"\\", StringComparison.Ordinal)
                                      || (cfg.UseMulticajaApiOnlyClient && cfg.EsCajaAdicional);
            if (esMulticajaCliente && !string.IsNullOrWhiteSpace(apiBase))
            {
                progreso.Report((60, "Probando conexión con la API del servidor..."));
                try
                {
                    if (!await ProbarApiAsync(apiBase).ConfigureAwait(true))
                    {
                        var seguir = MessageBox.Show(
                            "La API del servidor no respondió correctamente.\n\n" +
                            "Probablemente el servicio 'GrunflexPOSAPI' no está corriendo en la caja principal " +
                            "o el firewall bloquea el puerto 7279.\n\n" +
                            "¿Continuar de todos modos? La sincronización en vivo no funcionará hasta que se solucione.",
                            "Grunflex POS",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);
                        if (seguir != MessageBoxResult.Yes) return false;
                    }
                }
                catch (Exception ex)
                {
                    var seguir = MessageBox.Show(
                        "No se pudo contactar la API del servidor:\n\n" +
                        ex.Message + "\n\n" +
                        "¿Continuar de todos modos? La sincronización en vivo no funcionará.",
                        "Grunflex POS",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (seguir != MessageBoxResult.Yes) return false;
                }
            }

            progreso.Report((80, "Cerrando servicios en uso..."));
            await Task.Delay(150);

            try { App.Connectivity?.Stop(); } catch { }
            try { App.LicenseSync?.Stop(); } catch { }
            try { App.OfflineQueue?.Stop(); } catch { }
            try { App.Backups?.Stop(); } catch { }

            progreso.Report((95, "Configuración aplicada. Reiniciando POS..."));
            await Task.Delay(150);

            return true;
        }

        private static async Task<bool> ProbarApiAsync(string apiBase)
        {
            if (string.IsNullOrWhiteSpace(apiBase))
                return false;
            var probe = apiBase.TrimEnd('/') + "/health/live";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var resp = await http.GetAsync(probe).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }

        private static string ReiniciarServicioApi()
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                    return "Reinicio de servicio solo en Windows.";
#pragma warning disable CA1416
                using var sc = new ServiceController("GrunflexPOSAPI");
                try { _ = sc.Status; }
                catch { return "Servicio GrunflexPOSAPI no instalado."; }

                if (sc.Status == ServiceControllerStatus.Running)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                }

                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                return "Servicio GrunflexPOSAPI reiniciado.";
#pragma warning restore CA1416
            }
            catch (Exception ex)
            {
                return "No se pudo reiniciar GrunflexPOSAPI: " + ex.Message;
            }
        }

        private static string? ExtraerDataSourceSqlite(string cs)
        {
            if (string.IsNullOrWhiteSpace(cs)) return null;
            foreach (var parte in cs.Split(';'))
            {
                var t = parte.Trim();
                if (t.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
                    return t.Substring("Data Source=".Length).Trim();
                if (t.StartsWith("Filename=", StringComparison.OrdinalIgnoreCase))
                    return t.Substring("Filename=".Length).Trim();
            }
            return null;
        }

        private void DeshabilitarBotones(bool ocupado)
        {
            BtnActualizarConexion.IsEnabled = !ocupado;
            BtnCambiarNombre.IsEnabled = !ocupado && _seleccionada != null;
            BtnEliminarCaja.IsEnabled = !ocupado && _seleccionada != null;
            BtnConectarMasCajas.IsEnabled = !ocupado;
            BtnConectarServidor.IsEnabled = !ocupado;
            ListaCajas.IsEnabled = !ocupado;
        }

        private static void RelanzarPos()
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exe))
                {
                    exe = Path.Combine(AppContext.BaseDirectory,
                        Path.GetFileName(Assembly.GetEntryAssembly()?.Location ?? "GrunflexPOS2.exe"));
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exe!,
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log("CajasView.RelanzarPos: no se pudo iniciar el nuevo proceso", ex);
            }

            try { System.Windows.Application.Current.Shutdown(); } catch { Environment.Exit(0); }
        }
    }
}