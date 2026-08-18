using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Threading.Tasks; 
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.EntityFrameworkCore;
using GrunflexPOS2.UI;
using GrunflexPOS2.Data;
using GrunflexPOS2.Licensing;
using GrunflexPOS2.Models;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.API;
using GrunflexPOS2.Services.Connectivity;
using GrunflexPOS2.Services.Multicaja;
using GrunflexPOS2.Security;
using GrunflexPOS2.Startup;
using GrunflexPOS2.Views;

namespace GrunflexPOS2.Views
{
    public partial class CajaView : Window
    {
        private sealed class ResumenDetalleItem
        {
            public string Codigo { get; init; } = string.Empty;
            public string Nombre { get; init; } = string.Empty;
            public int Cantidad { get; init; }
            public string PrecioUnitario { get; init; } = string.Empty;
            public string Subtotal { get; init; } = string.Empty;
        }

        private VentasView? _ventasView;

        private ProductosView? _productosView;
        private InventarioView? _inventarioView;

        private ConfiguracionView? _configView;

        private enum NavSeccion
        {
            Ventas,
            Productos,
            Inventario,
            Reportes,
            Corte,
            Config
        }

        private readonly struct NavGlassTheme
        {
            public Color Accent { get; init; }
            public Color AccentLight { get; init; }
            public Color IconBg { get; init; }
            public Color ChevronBg { get; init; }
            public Color ChevronFg { get; init; }
            public Color Glow { get; init; }
        }

        private static NavGlassTheme VentasNavTheme() => BrandThemeService.IsMulticajaTheme
            ? new()
            {
                Accent = Color.FromRgb(0x25, 0x63, 0xEB),
                AccentLight = Color.FromRgb(0x60, 0xA5, 0xFA),
                IconBg = Color.FromRgb(0x17, 0x25, 0x54),
                ChevronBg = Color.FromRgb(0x1E, 0x3A, 0x8A),
                ChevronFg = Color.FromRgb(0x93, 0xC5, 0xFD),
                Glow = Color.FromRgb(0x3B, 0x82, 0xF6)
            }
            : new()
            {
                Accent = Color.FromRgb(0x10, 0xB9, 0x81),
                AccentLight = Color.FromRgb(0x6E, 0xE7, 0xB7),
                IconBg = Color.FromRgb(0x05, 0x2E, 0x1B),
                ChevronBg = Color.FromRgb(0x06, 0x4E, 0x3B),
                ChevronFg = Color.FromRgb(0x6E, 0xE7, 0xB7),
                Glow = Color.FromRgb(0x34, 0xD3, 0x99)
            };

        private static NavGlassTheme ThemeFor(NavSeccion seccion) => seccion switch
        {
            NavSeccion.Ventas => VentasNavTheme(),
            NavSeccion.Productos => new()
            {
                Accent = Color.FromRgb(0x05, 0x96, 0x69),
                AccentLight = Color.FromRgb(0x34, 0xD3, 0x99),
                IconBg = Color.FromRgb(0x05, 0x2E, 0x1B),
                ChevronBg = Color.FromRgb(0x06, 0x4E, 0x3B),
                ChevronFg = Color.FromRgb(0x6E, 0xE7, 0xB7),
                Glow = Color.FromRgb(0x10, 0xB9, 0x81)
            },
            NavSeccion.Inventario => new()
            {
                Accent = Color.FromRgb(0xD9, 0x77, 0x06),
                AccentLight = Color.FromRgb(0xFB, 0xBF, 0x24),
                IconBg = Color.FromRgb(0x45, 0x1A, 0x03),
                ChevronBg = Color.FromRgb(0x78, 0x35, 0x0F),
                ChevronFg = Color.FromRgb(0xFC, 0xD3, 0x4D),
                Glow = Color.FromRgb(0xF5, 0x9E, 0x0B)
            },
            NavSeccion.Reportes => new()
            {
                Accent = Color.FromRgb(0x63, 0x66, 0xF1),
                AccentLight = Color.FromRgb(0xA5, 0xB4, 0xFC),
                IconBg = Color.FromRgb(0x1E, 0x1B, 0x4B),
                ChevronBg = Color.FromRgb(0x31, 0x2E, 0x81),
                ChevronFg = Color.FromRgb(0xC7, 0xD2, 0xFE),
                Glow = Color.FromRgb(0x63, 0x66, 0xF1)
            },
            NavSeccion.Corte => new()
            {
                Accent = Color.FromRgb(0xDB, 0x27, 0x77),
                AccentLight = Color.FromRgb(0xF9, 0xA8, 0xD4),
                IconBg = Color.FromRgb(0x50, 0x07, 0x24),
                ChevronBg = Color.FromRgb(0x83, 0x18, 0x43),
                ChevronFg = Color.FromRgb(0xFB, 0xCF, 0xE8),
                Glow = Color.FromRgb(0xEC, 0x48, 0x99)
            },
            _ => new()
            {
                Accent = Color.FromRgb(0x64, 0x74, 0x8B),
                AccentLight = Color.FromRgb(0x94, 0xA3, 0xB8),
                IconBg = Color.FromRgb(0x1E, 0x29, 0x3B),
                ChevronBg = Color.FromRgb(0x33, 0x41, 0x55),
                ChevronFg = Color.FromRgb(0xCB, 0xD5, 0xE1),
                Glow = Color.FromRgb(0x64, 0x74, 0x8B)
            }
        };

        private static SolidColorBrush NavBrush(Color color) => new(color);

        private decimal _totalActual = 0;
        private DispatcherTimer? _redTimer;
        private DispatcherTimer? _cajaTimer;
        private DispatcherTimer? _alertasTimer;
        private DispatcherTimer? _ordinalRemotoTimer;
        private int _ordinalRemotoCache;
        private bool _ordinalRemotoListo;
        private bool _permitirCerrarVentana;
        private bool _resumenCobroRespaldado;
        private string _respaldoArticulos = "0";
        private string _respaldoSubtotal = "$0";
        private string _respaldoDescuento = "$0";
        private string _respaldoTotal = "$0";
        private string[] _respaldoDetalleLineas = Array.Empty<string>();

        public CajaView()
        {
            InitializeComponent();
            Loaded += CajaView_Loaded;
            Closing += CajaView_Closing;
        }

        public void PermitirCerrar()
        {
            _permitirCerrarVentana = true;
            _ordinalRemotoTimer?.Stop();
        }

        private void CajaView_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_permitirCerrarVentana)
                return;

            // Protege contra cierres involuntarios del POS.
            e.Cancel = true;
        }

        private void CajaView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                SoporteContexto.Modulo = "Caja";
                SoporteContexto.Accion = "Inicio";

                var sesion = App.ObtenerSesionCajaAbiertaVisual();

                if (sesion == null)
                {
                    MessageBox.Show("No hay caja abierta. Debe iniciar sesión correctamente.");
                    System.Windows.Application.Current.Shutdown();
                    return;
                }

                CargarVentasInicial();
                RefrescarLogoSuperior();
                RefrescarEstadoPremium();
                ActualizarEstadoCajaUI();
                IniciarMonitorRed();
                IniciarMonitorCaja();
                IniciarReloj(); // 🔥 NUEVO
                _alertasTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
                _alertasTimer.Tick += (_, _) => ActualizarTiraAlertas();
                _alertasTimer.Start();
                _ = StartupCloudSyncAsync();
                AplicarPermisosNav();

                if (MulticajaRuntime.UseApiOnlyClient)
                {
                    _ordinalRemotoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
                    _ordinalRemotoTimer.Tick += (_, _) => _ = RefrescarOrdinalCajaDesdeApiAsync();
                    _ordinalRemotoTimer.Start();
                    _ = RefrescarOrdinalCajaDesdeApiAsync();
                }

                AjustarLayoutResponsive();
                AplicarModuloInicioPendiente();
            }
            catch (Exception ex)
            {
                SoporteContexto.Error = ex.Message;
                MessageBox.Show("Error al iniciar Caja:\n" + ex.Message);
                System.Windows.Application.Current.Shutdown();
            }
        }

        private void AplicarModuloInicioPendiente()
        {
            var module = App.PendingStartupModule;
            if (string.IsNullOrWhiteSpace(module))
                return;

            App.PendingStartupModule = null;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    switch (module)
                    {
                        case StartupNavigation.Ventas:
                            Ventas_Click(this, new RoutedEventArgs());
                            break;
                        case StartupNavigation.Inventario:
                            Inventario_Click(this, new RoutedEventArgs());
                            break;
                        case StartupNavigation.ReporteVentasDelDia:
                            VentasDelDia_Click(this, new RoutedEventArgs());
                            break;
                        case var m when StartupNavigation.OpensDashboard(m):
                            Dashboard_Click(this, new RoutedEventArgs());
                            break;
                    }
                }
                catch (Exception ex)
                {
                    SoporteContexto.Error = ex.Message;
                }
            }), DispatcherPriority.ApplicationIdle);
        }

        private void CajaView_SizeChanged(object sender, SizeChangedEventArgs e) =>
            AjustarLayoutResponsive();

        private void AjustarLayoutResponsive() =>
            CajaResponsiveHelper.Ajustar(this, GridColumnasPrincipal, MainContent, PanelCobro, TotalText);

        private void AplicarPermisosNav()
        {
            BtnNavConfig.Visibility = UsuarioPermisos.PuedeAccederConfiguracion()
                ? Visibility.Visible
                : Visibility.Collapsed;
            BtnNavInventario.Visibility = UsuarioPermisos.PuedeAjustarInventario()
                ? Visibility.Visible
                : Visibility.Collapsed;
            BtnNavReportes.Visibility = UsuarioPermisos.PuedeVerReportes()
                ? Visibility.Visible
                : Visibility.Collapsed;
            BtnNavProductos.Visibility = UsuarioPermisos.PuedeGestionarProductos()
                ? Visibility.Visible
                : Visibility.Collapsed;
            BtnVentasDelDia.Visibility = UsuarioPermisos.PuedeVerReportes()
                ? Visibility.Visible
                : Visibility.Collapsed;
            BtnCobrar.Visibility = UsuarioPermisos.PuedeCobrar()
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ActualizarEstadoCajaUI()
        {
            var sesion = App.ObtenerSesionCajaAbiertaVisual();

            if (sesion == null)
            {
                DotHeaderCaja.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                TxtHeaderNumeroCaja.Text = "Sin caja";
                TxtHeaderEstadoCaja.Text = "Cerrada";
                TxtHeaderEstadoCaja.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));

                TxtCajero.Text = "";
                DineroCajaText.Text = "$0";

                BtnCerrarCaja.Visibility = Visibility.Collapsed;
                BtnAbrirCaja.Visibility = Visibility.Visible;
            }
            else
            {
                DotHeaderCaja.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                // Sufijo "(principal)" / "(adicional)" para que en pantalla quede claro qué
                // rol tiene este equipo en la topología multicaja. AHORA usamos el rol
                // persistido por el instalador (AppConfig.EsCajaPrincipal) en vez de
                // mirar la cadena de conexión, porque la cadena puede caer a local en una
                // caja adicional mal configurada y dar "principal" falso.
                var cfgCaja = Data.AppConfig.Cargar();
                var rol = cfgCaja.EsCajaPrincipal ? "principal" : "adicional";

                // IMPORTANTE: nunca confiar en sesion.NumeroCaja para mostrar el número.
                // Hay sesiones viejas creadas con el algoritmo de hash que tienen valores
                // arbitrarios (ej. 16, 92). Recomputamos secuencial sobre la tabla Cajas
                // ordenadas por FechaCreacion para que la principal sea siempre "Caja 1",
                // la siguiente registrada "Caja 2", etc. Estable y consistente entre PCs.
                int numeroReal = ResolverNumeroCajaReal(sesion.CajaId, sesion.NumeroCaja);
                TxtHeaderNumeroCaja.Text = $"Caja {numeroReal} ({rol})";
                TxtHeaderEstadoCaja.Text = "Abierta";
                TxtHeaderEstadoCaja.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));

                TxtCajero.Text = App.UsuarioActual?.Username ?? "";

                decimal dinero =
                    sesion.MontoApertura +
                    sesion.TotalVentas +
                    sesion.TotalIngresos -
                    sesion.TotalRetiros;

                DineroCajaText.Text =
                    dinero.ToString("C0", new System.Globalization.CultureInfo("es-CL"));

                BtnCerrarCaja.Visibility = Visibility.Visible;
                BtnAbrirCaja.Visibility = Visibility.Collapsed;

                App.CajaActualId = sesion.CajaId;
            }
        }

        private void IniciarMonitorCaja()
        {
            _cajaTimer = new DispatcherTimer();
            _cajaTimer.Interval = TimeSpan.FromSeconds(1);
            _cajaTimer.Tick += (s, e) => ActualizarEstadoCajaUI();
            _cajaTimer.Start();
        }

        private void IniciarMonitorRed()
        {
            _redTimer = new DispatcherTimer();
            _redTimer.Interval = TimeSpan.FromSeconds(5);
            _redTimer.Tick += (s, e) => VerificarRed();
            _redTimer.Start();
        }

        /// <summary>
        /// Recalcula el "número humano" de la caja (1, 2, 3...) en runtime, ordenando
        /// las cajas por FechaCreacion. Si la BD no tiene la entrada (cadena rota,
        /// adicional sin red, etc.) devolvemos el último valor cacheado en la sesión
        /// como fallback para no romper la UI. Mantiene cero relación con el campo
        /// histórico Caja.NumeroCaja calculado por hash de MachineName.
        /// </summary>
        private int ResolverNumeroCajaReal(Guid cajaId, int fallback)
        {
            try
            {
                if (MulticajaRuntime.UseApiOnlyClient && _ordinalRemotoListo && _ordinalRemotoCache > 0)
                    return _ordinalRemotoCache;

                if (App.DbContext == null) return fallback;
                var todas = App.DbContext.Cajas
                    .AsNoTracking()
                    .OrderBy(c => c.FechaCreacion)
                    .ThenBy(c => c.Id)
                    .Select(c => c.Id)
                    .ToList();
                int idx = todas.IndexOf(cajaId);
                return idx >= 0 ? idx + 1 : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private async Task RefrescarOrdinalCajaDesdeApiAsync()
        {
            try
            {
                if (!MulticajaRuntime.UseApiOnlyClient)
                    return;
                var ses = App.ObtenerSesionCajaAbiertaVisual();
                if (ses == null)
                    return;
                var ord = await MulticajaOperacionesClient.ObtenerOrdinalCajaAsync(ses.CajaId).ConfigureAwait(false);
                if (ord is > 0 and < 10_000)
                {
                    _ordinalRemotoCache = ord.Value;
                    _ordinalRemotoListo = true;
                    await Dispatcher.InvokeAsync(ActualizarEstadoCajaUI);
                }
            }
            catch
            {
                /* best-effort */
            }
        }

        private async void BtnActualizarDatos_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BtnActualizarDatos.IsEnabled = false;
                if (MulticajaRuntime.UseApiOnlyClient)
                {
                    var r = App.MulticajaBackground != null
                        ? await App.MulticajaBackground.Sync.SyncNowAsync(
                            GrunflexPOS2.Services.Multicaja.Sync.SyncReason.Manual).ConfigureAwait(true)
                        : await MulticajaShadowCatalogSync.PullTodoAsync().ConfigureAwait(true);
                    if (!r.Ok)
                    {
                        MessageBox.Show(
                            "No se pudo sincronizar con el servidor:\n" + r.Error,
                            "Actualizar",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }

                    await RefrescarOrdinalCajaDesdeApiAsync().ConfigureAwait(true);
                }

                RefrescarModulosAbiertosDesdeServidor();
                VerificarRed();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al actualizar:\n" + ex.Message, "Actualizar", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                BtnActualizarDatos.IsEnabled = true;
            }
        }

        private void RefrescarModulosAbiertosDesdeServidor()
        {
            try
            {
                _productosView?.RefrescarCatalogo();
                _inventarioView?.RefrescarDesdeBase();
                _ventasView?.RefrescarAutocompletadoProductos();
            }
            catch
            {
                /* no bloquear */
            }
        }

        /// <summary>
        /// Estado del badge "Red" del top bar.
        /// El rol se decide por el TerminalRole persistido (instalador) y solo cae al
        /// chequeo de UNC como heurística secundaria. Esto evita el bug donde una caja
        /// adicional mal configurada (cadena local accidental) se mostraba como
        /// "Servidor local" en ambos equipos.
        ///
        /// Servidor principal → "Servidor local" (verde, sin red que medir).
        /// Caja adicional      → "Red OK" / "Red lenta" / "Sin Red" según ConnectivityMonitor.
        /// </summary>
        private void VerificarRed()
        {
            try
            {
                var cfg = Data.AppConfig.Cargar();

                if (cfg.EsCajaPrincipal)
                {
                    int adicionales = ContarCajasAdicionales();
                    SetBadgeOk("Servidor local");
                    SetBadgeMulticaja(
                        adicionales > 0
                            ? $"🔗 {adicionales} caja(s) adicional(es) registrada(s)"
                            : "🔗 Solo caja principal (use Administrar cajas → Conectar más cajas para enlazar otras)",
                        ok: true);
                    return;
                }

                var state = App.Connectivity?.State ?? ConnectivityState.Unknown;
                switch (state)
                {
                    case ConnectivityState.Online:
                        SetBadgeOk("Red OK");
                        break;
                    case ConnectivityState.Degraded:
                        SetBadgeWarn("Red lenta");
                        break;
                    case ConnectivityState.Offline:
                        SetBadgeError("Sin Red");
                        break;
                    default:
                        SetBadgeWarn("Conectando…");
                        break;
                }

                // Segundo badge en cajas adicionales: muestra el destino real al que
                // estamos conectados. Sin esto el operador no tenía forma visible de
                // saber DESDE DÓNDE sale la conexión multicaja.
                var host = ExtraerHostDestino(cfg);
                if (!string.IsNullOrWhiteSpace(host))
                {
                    bool okConn = state == ConnectivityState.Online;
                    SetBadgeMulticaja($"🔗 Servidor {host}", ok: okConn);
                }
                else
                {
                    SetBadgeMulticaja("🔗 Sin servidor", ok: false);
                }
            }
            catch
            {
                SetBadgeError("Sin Red");
                SetBadgeMulticaja("🔗 Sin servidor", ok: false);
            }
        }

        private int ContarCajasAdicionales()
        {
            try
            {
                if (App.DbContext == null) return 0;
                // En la principal solemos tener al menos 1 caja (la propia). Reportamos
                // como "adicionales" todas las demás.
                int total = App.DbContext.Cajas.AsNoTracking().Count();
                return Math.Max(0, total - 1);
            }
            catch
            {
                return 0;
            }
        }

        private static string ExtraerHostDestino(Data.AppConfig cfg)
        {
            // Prioridad: host del UNC en la cadena de conexión. Si no hay, host de la API.
            var cs = cfg.ConnectionString ?? string.Empty;
            if (cs.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var resto = cs.Substring(2);
                var fin = resto.IndexOfAny(new[] { '\\', ';', ' ' });
                var host = fin > 0 ? resto.Substring(0, fin) : resto;
                if (!string.IsNullOrWhiteSpace(host)) return host;
            }
            try
            {
                if (Uri.TryCreate(cfg.ApiBaseUrl, UriKind.Absolute, out var u))
                    return u.Host;
            }
            catch { }
            return string.Empty;
        }

        private void SetBadgeMulticaja(string text, bool ok)
        {
            // Pintamos el badge multicaja a la izquierda del badge de red. Si los
            // controles XAML no existen todavía (skin vieja), salimos sin error.
            try
            {
                if (this.FindName("BadgeMulticaja") is Border b &&
                    this.FindName("TxtBadgeMulticaja") is TextBlock t)
                {
                    t.Text = text;
                    if (ok)
                    {
                        b.Background = new SolidColorBrush(Color.FromRgb(220, 252, 231));
                        b.BorderBrush = new SolidColorBrush(Color.FromRgb(134, 239, 172));
                        t.Foreground = new SolidColorBrush(Color.FromRgb(22, 101, 52));
                    }
                    else
                    {
                        b.Background = new SolidColorBrush(Color.FromRgb(254, 226, 226));
                        b.BorderBrush = new SolidColorBrush(Color.FromRgb(252, 165, 165));
                        t.Foreground = new SolidColorBrush(Color.FromRgb(127, 29, 29));
                    }
                    b.Visibility = Visibility.Visible;
                }
            }
            catch
            {
                // best-effort, no debe romper el ciclo del timer.
            }
        }

        private void SetBadgeOk(string text)
        {
            BadgeRed.Background = new SolidColorBrush(Color.FromRgb(243, 244, 246));
            BadgeRed.BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240));
            EstadoRedText.Text = text;
            EstadoRedText.Foreground = new SolidColorBrush(Color.FromRgb(6, 95, 70));
            TxtBadgeWifi.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        }

        private void SetBadgeWarn(string text)
        {
            BadgeRed.Background = new SolidColorBrush(Color.FromRgb(254, 243, 199));
            BadgeRed.BorderBrush = new SolidColorBrush(Color.FromRgb(252, 211, 77));
            EstadoRedText.Text = text;
            EstadoRedText.Foreground = new SolidColorBrush(Color.FromRgb(120, 53, 15));
            TxtBadgeWifi.Foreground = new SolidColorBrush(Color.FromRgb(217, 119, 6));
        }

        private void SetBadgeError(string text)
        {
            BadgeRed.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            BadgeRed.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            EstadoRedText.Text = text;
            EstadoRedText.Foreground = Brushes.White;
            TxtBadgeWifi.Foreground = Brushes.White;
        }

        private void BtnAbrirCaja_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Debe cerrar sesión y volver a ingresar para abrir una nueva caja.");
        }

        private async void BtnCerrarCaja_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SoporteContexto.Accion = "Cerrar caja";

                var sesion = App.ObtenerSesionCajaAbiertaVisual();

                if (sesion == null)
                {
                    MessageBox.Show("No hay caja abierta.");
                    return;
                }

                decimal esperado =
                    sesion.MontoApertura +
                    sesion.TotalVentas +
                    sesion.TotalIngresos -
                    sesion.TotalRetiros;

                string input = Microsoft.VisualBasic.Interaction.InputBox(
                    $"Monto esperado: {esperado.ToString("C0", new System.Globalization.CultureInfo("es-CL"))}\n\nIngrese monto contado:",
                    "Cierre de Caja",
                    esperado.ToString("0"));

                if (string.IsNullOrWhiteSpace(input))
                    return;

                input = input.Replace(".", "").Replace(",", "");

                if (!decimal.TryParse(input, out decimal contado))
                {
                    SoporteContexto.Error = "Monto inválido en cierre de caja";
                    MessageBox.Show("Monto inválido.");
                    return;
                }

                decimal diferencia = contado - esperado;

                var confirm = MessageBox.Show(
                    $"Esperado: {esperado:C0}\n" +
                    $"Contado: {contado:C0}\n" +
                    $"Diferencia: {diferencia:C0}\n\n" +
                    $"¿Confirmar cierre de caja?",
                    "Confirmar cierre",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes)
                    return;

                if (MulticajaRuntime.UseApiOnlyClient)
                {
                    if (App.UsuarioActual == null)
                    {
                        MessageBox.Show("No hay usuario para registrar el cierre.");
                        return;
                    }

                    var body = new MulticajaCierreCajaRequest
                    {
                        RequestId = Guid.NewGuid().ToString("N"),
                        CajaId = sesion.CajaId,
                        CajaSesionId = sesion.Id,
                        UsuarioCierreId = App.UsuarioActual.Id,
                        MontoContado = contado
                    };

                    MulticajaCierreCajaResponse? resp = null;
                    try
                    {
                        resp = await MulticajaOperacionesClient.CerrarSesionAsync(body).ConfigureAwait(true);
                    }
                    catch (HttpRequestException ex)
                    {
                        PosDiagnostics.Log("multicaja.cierre: error de red.", ex);
                        MulticajaOfflineEnqueue.TryEnqueue("multicaja-cierre-sesion", body, body.RequestId);
                        MessageBox.Show(
                            "No se pudo contactar al servidor. Si la cola offline está habilitada, el cierre quedó pendiente de envío.");
                        return;
                    }
                    catch (TaskCanceledException ex)
                    {
                        PosDiagnostics.Log("multicaja.cierre: timeout.", ex);
                        MulticajaOfflineEnqueue.TryEnqueue("multicaja-cierre-sesion", body, body.RequestId);
                        MessageBox.Show(
                            "Timeout al cerrar. Si la cola offline está habilitada, el cierre quedó pendiente de envío.");
                        return;
                    }

                    if (resp == null || !resp.Ok)
                    {
                        PosDiagnostics.Log($"multicaja.cierre error code={resp?.ErrorCode} msg={resp?.Error}");
                        MessageBox.Show(resp?.Error ?? "No se pudo cerrar la caja en el servidor.");
                        return;
                    }

                    PosDiagnostics.Log($"multicaja.cierre ok esperado={resp.Esperado} dif={resp.Diferencia}");
                    App.MulticajaSesionEnServidor = null;
                    MessageBox.Show("Caja cerrada correctamente (servidor central).");
                }
                else
                {
                    var cajaService = new CajaService(App.DbContext);
                    cajaService.CerrarCaja(sesion.Id, contado);
                    MessageBox.Show("Caja cerrada correctamente.");
                }

                ActualizarEstadoCajaUI();

                var salir = MessageBox.Show(
                    "¿Desea cerrar sesión?",
                    "Cerrar sesión",
                    MessageBoxButton.YesNo);

                if (salir == MessageBoxResult.Yes)
                {
                    App.Logout();
                }
            }
            catch (Exception ex)
            {
                SoporteContexto.Error = ex.Message;
                MessageBox.Show("Error al cerrar caja:\n" + ex.Message);
            }
        }

        private void Minimizar_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Maximizar_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void Cerrar_Click(object sender, RoutedEventArgs e)
        {
            _permitirCerrarVentana = true;
            Close();
        }

        private void BarraSuperior_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void Ventas_Click(object sender, RoutedEventArgs e)
        {
            SoporteContexto.Accion = "Ir a ventas";
            CargarVentasInicial();
        }

        private void Productos_Click(object sender, RoutedEventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureProductos())
                return;

            SoporteContexto.Accion = "Ver productos";

            if (_productosView == null)
                _productosView = new ProductosView();

            NavegarPantalla(_productosView, esPantallaPrincipal: false);
        }

        private void Inventario_Click(object sender, RoutedEventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureInventario())
                return;

            SoporteContexto.Accion = "Abrir inventario";
            AbrirInventario();
        }

        private void Dashboard_Click(object sender, RoutedEventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureReportes())
                return;

            SoporteContexto.Accion = "Ver dashboard";
            NavegarPantalla(new DashboardView(), esPantallaPrincipal: false);
        }

        private void Corte_Click(object sender, RoutedEventArgs e)
        {
            SoporteContexto.Accion = "Ver corte";

            var sesion = App.ObtenerSesionCajaAbiertaVisual();

            if (sesion == null)
            {
                MessageBox.Show("No hay una caja abierta.");
                return;
            }

            var diaService = new DiaComercialService(new TimeSpan(8, 0, 0));

            var corteService = new CorteService(
                diaService,
                App.VentaService);

            var resumen = corteService.GenerarResumen(
                "Caja",
                "Sistema",
                sesion.MontoApertura);

            NavegarPantalla(new CorteView(resumen), esPantallaPrincipal: false);
        }
        private void IniciarReloj()
        {
            var timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);

            timer.Tick += (s, e) =>
            {
                HoraActualText.Text = DateTime.Now.ToString("HH:mm");
            };

            timer.Start();
        }
        private void Compras_Click(object sender, RoutedEventArgs e) { }

        private void Configuracion_Click(object sender, RoutedEventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureConfiguracion())
                return;

            SoporteContexto.Accion = "Configuración";

            if (_configView == null)
            {
                _configView = new ConfiguracionView();

                _configView.OnNavigate += (vista) =>
                {
                    MainContent.Content = CrearVistaConfiguracionConRetorno(vista);
                };
            }

            NavegarPantalla(_configView, esPantallaPrincipal: false);
        }

        private UIElement CrearVistaConfiguracionConRetorno(UserControl vista)
        {
            OcultarBotonesDuplicados(vista);
            vista.Loaded -= VistaConfiguracion_Loaded;
            vista.Loaded += VistaConfiguracion_Loaded;

            var layout = new DockPanel
            {
                LastChildFill = true
            };

            var barra = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(11, 58, 130)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(8, 45, 102)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 8, 10, 8)
            };
            DockPanel.SetDock(barra, Dock.Top);

            var btnVolver = new Button
            {
                Content = "🏠 Mostrar todas las opciones",
                Padding = new Thickness(14, 6, 14, 6),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(245, 189, 71)),
                Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39)),
                FontWeight = FontWeights.SemiBold,
                BorderBrush = new SolidColorBrush(Color.FromRgb(214, 158, 46)),
                BorderThickness = new Thickness(1)
            };
            btnVolver.Click += (_, _) => NavegarPantalla(_configView!, esPantallaPrincipal: false);

            barra.Child = btnVolver;
            layout.Children.Add(barra);
            layout.Children.Add(vista);

            return layout;
        }

        private void VistaConfiguracion_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not UserControl vista)
                return;

            // Ejecutar cuando el visual tree ya está materializado.
            vista.Dispatcher.BeginInvoke(new Action(() =>
            {
                OcultarBotonesDuplicados(vista);
            }), DispatcherPriority.Loaded);
        }

        private static void OcultarBotonesDuplicados(DependencyObject raiz)
        {
            if (raiz is Button b)
            {
                string texto = (b.Content?.ToString() ?? string.Empty).Trim().ToLowerInvariant();
                if (texto.Contains("mostrar todas las opciones"))
                {
                    b.Visibility = Visibility.Collapsed;
                }
            }

            if (raiz is Visual || raiz is System.Windows.Media.Media3D.Visual3D)
            {
                int n = VisualTreeHelper.GetChildrenCount(raiz);
                for (int i = 0; i < n; i++)
                    OcultarBotonesDuplicados(VisualTreeHelper.GetChild(raiz, i));
            }

            foreach (var child in LogicalTreeHelper.GetChildren(raiz).OfType<DependencyObject>())
                OcultarBotonesDuplicados(child);
        }

        private async Task StartupCloudSyncAsync()
        {
            try
            {
                CloudBackupCoordinator.TryStartMainWindow();
                await LicensingCloudService.TryRefreshAsync(silent: true).ConfigureAwait(true);
                RefrescarEstadoPremium();
            }
            catch
            {
                /* sincronización opcional en arranque */
            }
        }

        private void Web_Click(object sender, RoutedEventArgs e)
        {
            if (!App.LicenseState.OnlineSupport)
            {
                MessageBox.Show(
                    "El acceso web integrado requiere la opción de soporte en línea contratada.",
                    "Soporte en línea",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var ventana = new WebViewWindow("https://www.google.cl");
            ventana.Owner = this;
            ventana.ShowDialog();
        }

        private void CargarVentasInicial()
        {
            if (_ventasView == null)
            {
                _ventasView = new VentasView();
                _ventasView.ResumenActualizado += VentasView_ResumenActualizado;
            }

            NavegarPantalla(_ventasView, esPantallaPrincipal: true);
        }

        public void RefrescarLogoSuperior()
        {
            try
            {
                ImgLogoTop.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo_login.png"));
                ImgLogoTop.Visibility = Visibility.Visible;
                TxtTituloTop.Visibility = Visibility.Collapsed;
            }
            catch
            {
                ImgLogoTop.Source = new BitmapImage(new Uri("/Assets/logo_login.png", UriKind.Relative));
                ImgLogoTop.Visibility = Visibility.Visible;
                TxtTituloTop.Visibility = Visibility.Collapsed;
            }
        }

        private void YouTubeWidget_OpenFullScreenRequested(object sender, EventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureConfiguracion())
                return;
            NavegarPantalla(new YouTubeView(), esPantallaPrincipal: false);
        }

        private void BtnPremiumMas_Click(object sender, RoutedEventArgs e)
        {
            var r = GrunflexDialogs.Show(
                "Las funciones premium incluyen navegación web integrada, soporte en línea y opciones avanzadas.\n\n¿Desea ingresar o actualizar su licencia?",
                "Funciones Premium",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information,
                this);

            if (r != MessageBoxResult.Yes)
                return;

            var dialog = new InsertarLicenciaWindow { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                GrunflexDialogs.Show(
                    "Licencia aplicada correctamente.",
                    "Licencia",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information,
                    this);
            }

            RefrescarEstadoPremium();
        }

        private void VentasView_ResumenActualizado(decimal subtotalLista, decimal montoDescuento, decimal totalNeto, int articulos)
        {
            _totalActual = totalNeto;

            ResumenArticulos.Text = articulos.ToString();

            var culturaCL = new System.Globalization.CultureInfo("es-CL");

            ResumenSubtotal.Text = subtotalLista.ToString("C0", culturaCL);
            ResumenDescuento.Text = montoDescuento.ToString("C0", culturaCL);
            TotalText.Text = totalNeto.ToString("C0", culturaCL);
            CargarDetalleResumen(culturaCL);

            SoporteContexto.Venta = totalNeto;
        }

        private void CargarDetalleResumen(System.Globalization.CultureInfo culturaCL)
        {
            if (_ventasView == null)
            {
                ResumenDetallePanel.Children.Clear();
                return;
            }

            var detalle = _ventasView.ObtenerLineasActuales()
                .Select(i => new ResumenDetalleItem
                {
                    Codigo = i.CodigoBarras,
                    Nombre = i.Producto,
                    Cantidad = i.Cantidad,
                    PrecioUnitario = i.Precio.ToString("C0", culturaCL),
                    Subtotal = i.Importe.ToString("C0", culturaCL)
                })
                .ToList();

            ResumenDetallePanel.Children.Clear();

            if (detalle.Count == 0)
            {
                ResumenDetallePanel.Children.Add(new TextBlock
                {
                    Text = "Sin productos",
                    Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                    Margin = new Thickness(0, 2, 0, 0)
                });
                return;
            }

            foreach (var item in detalle)
            {
                var fila = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 2),
                    Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55))
                };

                fila.Inlines.Add(new Run($"{item.Codigo} | ")
                {
                    FontWeight = FontWeights.SemiBold
                });
                fila.Inlines.Add(new Run($"{item.Nombre} | "));
                fila.Inlines.Add(new Run($"x{item.Cantidad} | "));
                fila.Inlines.Add(new Run($"{item.PrecioUnitario} | "));
                fila.Inlines.Add(new Run(item.Subtotal)
                {
                    FontWeight = FontWeights.Bold
                });

                ResumenDetallePanel.Children.Add(fila);
            }
        }

        private void Cobrar_Click(object sender, RoutedEventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureCobrar())
                return;

            SoporteContexto.Accion = "Cobrar";
            AbrirCobro();
        }

        private async void AbrirCobro()
        {
            if (!UsuarioPermisosGate.EnsureCobrar())
                return;

            if (_ventasView == null || _totalActual <= 0)
                return;

            try
            {
                string metodoPago = "Efectivo";
                bool imprimirTicket = true;
                bool esConsumoPersonal = false;
                while (true)
                {
                    var lineas = _ventasView.ObtenerLineasActuales();
                    var articulos = lineas.Sum(x => x.Cantidad);
                    var brutoLista = lineas.Sum(x => Math.Round(x.Precio * x.Cantidad, 2));
                    var netoLista = lineas.Sum(x => x.Importe);
                    var descuentosBarra = Math.Max(0m, Math.Round(brutoLista - netoLista, 2));

                    var ventana = new CobroView(_totalActual, metodoPago, articulos, descuentosBarra);
                    ventana.Owner = this;

                    if (ventana.ShowDialog() == true)
                    {
                        esConsumoPersonal = ventana.ConsumoPersonal;
                        if (esConsumoPersonal)
                        {
                            metodoPago = "Consumo personal";
                            imprimirTicket = false;
                        }
                        else
                        {
                            metodoPago = string.IsNullOrWhiteSpace(ventana.MetodoPagoSeleccionado)
                                ? metodoPago
                                : ventana.MetodoPagoSeleccionado;
                            imprimirTicket = ventana.ImprimirTicket;
                        }
                        break;
                    }

                    if (ventana.EditarCompraSolicitada)
                        return;

                    if (ventana.DescuentoSolicitadoPorcentaje.HasValue && _ventasView != null)
                    {
                        _ventasView.AplicarDescuentoGlobal(ventana.DescuentoSolicitadoPorcentaje.Value);
                        _totalActual = _ventasView.ObtenerTotal();
                        continue;
                    }

                    return;
                }

                if (!esConsumoPersonal && string.Equals(metodoPago, "Tarjeta", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var api = new GrunflexPOS2.Services.API.PagoApiService();

                        int numeroTicket = App.VentaService.GenerarNumeroTicket();

                        MessageBox.Show($"TOTAL ACTUAL: {_totalActual}");

                        decimal montoEnviar = Math.Round(_totalActual, 0);

                        MessageBox.Show($"TOTAL ENVIADO AL POS: {montoEnviar}");

                        var id = await api.CrearPagoAsync(montoEnviar, numeroTicket.ToString());

                        if (id == null)
                        {
                            MessageBox.Show("Error al iniciar pago.");
                            return;
                        }

                        MessageBox.Show("Esperando confirmación del POS...");

                        GrunflexPOS2.Services.API.PagoTransaccion? estado = null;

                        for (int i = 0; i < 20; i++)
                        {
                            await Task.Delay(1000);

                            estado = await api.ObtenerEstadoAsync(id.Value);

                            if (estado != null && estado.Estado != "PENDIENTE")
                                break;
                        }

                        if (estado == null)
                        {
                            MessageBox.Show("Error consultando estado del pago.");
                            return;
                        }

                        if (estado.Estado != "APROBADO")
                        {
                            MessageBox.Show($"Pago rechazado.\nMonto enviado: {montoEnviar}");
                            return;
                        }

                        MessageBox.Show($"Pago aprobado\nCódigo: {estado.CodigoAutorizacion}");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Error en pago:\n" + ex.Message);
                        return;
                    }
                }

                int numeroTicketFinal = App.VentaService.GenerarNumeroTicket();

                var sesion = App.ObtenerSesionCajaAbiertaVisual();

                if (sesion == null)
                {
                    MessageBox.Show("No hay una caja abierta.");
                    return;
                }

                var nuevaVenta = new Venta
                {
                    NumeroTicket = numeroTicketFinal,
                    Fecha = DateTime.UtcNow,
                    Total = _totalActual,
                    UsuarioId = App.UsuarioActual.Id,
                    CajaSesionId = sesion.Id,
                    MetodoPago = metodoPago,
                    EsConsumoPersonal = esConsumoPersonal,
                    Items = _ventasView.ObtenerItemsActuales().ToList()
                };

                if (!App.VentaService.GuardarVenta(nuevaVenta, out var errorVenta))
                {
                    MessageBox.Show(
                        string.IsNullOrWhiteSpace(errorVenta)
                            ? "No se pudo registrar la venta. Revise conexión con el servidor e intente de nuevo."
                            : errorVenta,
                        "Venta no registrada",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (!esConsumoPersonal && string.Equals(metodoPago, "Efectivo", StringComparison.OrdinalIgnoreCase))
                    CajonDineroService.IntentarAbrirEnCobroEfectivo();

                if (imprimirTicket)
                {
                    if (!TicketPdfService.TryGenerarTicketPDF(nuevaVenta, out var rutaTicket, out var errorTicket))
                    {
                        MessageBox.Show("Venta registrada, pero hubo un problema al generar ticket:\n" + errorTicket);
                    }
                    else
                    {
                        var okTicket = new TicketPdfSuccessWindow(
                            rutaTicket,
                            titulo: "Ticket generado correctamente",
                            subtitulo: "El ticket se ha guardado en la siguiente ubicación:")
                        {
                            Owner = this
                        };
                        okTicket.ShowDialog();
                    }
                }

                _ventasView.LimpiarVenta();
                _ventasView.RefrescarAutocompletadoProductos();
                _totalActual = 0;
            }
            catch (Exception ex)
            {
                SoporteContexto.Error = ex.Message;
                MessageBox.Show("Error en cobro:\n" + ex.Message);
            }
        }

        private void ReimprimirUltimo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var ventas = App.VentaService.ObtenerVentas();

                if (!ventas.Any())
                {
                    MessageBox.Show("No hay ventas.");
                    return;
                }

                var ultima = ventas.Last();
                if (!TicketPdfService.TryGenerarTicketPDF(ultima, out var rutaTicket, out var errorTicket))
                {
                    MessageBox.Show("No se pudo reimprimir el ticket:\n" + errorTicket);
                    return;
                }

                var dlg = new TicketPdfSuccessWindow(rutaTicket) { Owner = this };
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo reimprimir el ticket:\n" + ex.Message);
            }
        }

        private void VentasDelDia_Click(object sender, RoutedEventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureReportes())
                return;

            var ventana = new VentasDelDiaView();
            ventana.Owner = this;
            ventana.ShowDialog();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F1)
            {
                CargarVentasInicial();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F3)
            {
                if (!UsuarioPermisosGate.EnsureProductos())
                {
                    e.Handled = true;
                    return;
                }

                if (_productosView == null)
                    _productosView = new ProductosView();
                NavegarPantalla(_productosView, esPantallaPrincipal: false);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F4)
            {
                if (!UsuarioPermisosGate.EnsureInventario())
                {
                    e.Handled = true;
                    return;
                }

                AbrirInventario();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F12)
            {
                AbrirCobro();
                e.Handled = true;
            }
        }

        private void AbrirInventario()
        {
            if (!UsuarioPermisosGate.EnsureInventario())
                return;

            if (_inventarioView == null)
                _inventarioView = new InventarioView();

            NavegarPantalla(_inventarioView, esPantallaPrincipal: false);
        }

        private void NavegarPantalla(UIElement vista, bool esPantallaPrincipal)
        {
            MainContent.Content = vista;
            ActualizarIndicadorNav(vista);
            RefrescarEstadoPremium();

            if (esPantallaPrincipal)
                RestaurarResumenCobroSiCorresponde();
            else
                MostrarResumenCobroEnCero();
        }

        private void MarcarNav(NavSeccion seccion)
        {
            ApplyNavGlassItem(BdNavVentasShell, BdNavVentasIcon, TbNavVentasShortcut, TbNavVentasLabel, TbNavVentasDesc, BdNavVentasChevron, NavSeccion.Ventas, seccion);
            ApplyNavGlassItem(BdNavProductosShell, BdNavProductosIcon, TbNavProductosShortcut, TbNavProductosLabel, TbNavProductosDesc, BdNavProductosChevron, NavSeccion.Productos, seccion);
            ApplyNavGlassItem(BdNavInventarioShell, BdNavInventarioIcon, TbNavInventarioShortcut, TbNavInventarioLabel, TbNavInventarioDesc, BdNavInventarioChevron, NavSeccion.Inventario, seccion);
            ApplyNavGlassItem(BdNavReportesShell, BdNavReportesIcon, TbNavReportesShortcut, TbNavReportesLabel, TbNavReportesDesc, BdNavReportesChevron, NavSeccion.Reportes, seccion);
            ApplyNavGlassItem(BdNavCorteShell, BdNavCorteIcon, TbNavCorteShortcut, TbNavCorteLabel, TbNavCorteDesc, BdNavCorteChevron, NavSeccion.Corte, seccion);
            ApplyNavGlassItem(BdNavConfigShell, BdNavConfigIcon, TbNavConfigShortcut, TbNavConfigLabel, TbNavConfigDesc, BdNavConfigChevron, NavSeccion.Config, seccion);
        }

        private static void ApplyNavGlassItem(
            Border shell,
            Border iconBox,
            TextBlock shortcut,
            TextBlock label,
            TextBlock desc,
            Border chevron,
            NavSeccion section,
            NavSeccion active)
        {
            var theme = ThemeFor(section);
            var isActive = section == active;

            shell.Background = NavBrush(isActive ? theme.Accent : Oscurecer(theme.Accent, 0.72));
            shell.BorderBrush = NavBrush(isActive ? theme.AccentLight : theme.Accent);
            shell.BorderThickness = new Thickness(1);
            shell.Effect = null;

            iconBox.Background = NavBrush(isActive ? theme.AccentLight : theme.Accent);
            iconBox.BorderBrush = NavBrush(Colors.Transparent);
            iconBox.BorderThickness = new Thickness(0);

            if (!string.IsNullOrWhiteSpace(shortcut.Text))
                shortcut.Foreground = NavBrush(Colors.White);

            label.Foreground = NavBrush(Colors.White);
            desc.Foreground = NavBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));

            chevron.Background = NavBrush(isActive ? Oscurecer(theme.Accent, 0.55) : Oscurecer(theme.Accent, 0.85));
            if (chevron.Child is TextBlock chevronIcon)
                chevronIcon.Foreground = NavBrush(Colors.White);
        }

        private static Color Oscurecer(Color color, double factor)
        {
            factor = Math.Clamp(factor, 0, 1);
            return Color.FromRgb(
                (byte)(color.R * factor),
                (byte)(color.G * factor),
                (byte)(color.B * factor));
        }

        private void ActualizarIndicadorNav(UIElement vista)
        {
            switch (vista)
            {
                case VentasView:
                    MarcarNav(NavSeccion.Ventas);
                    break;
                case ProductosView:
                    MarcarNav(NavSeccion.Productos);
                    break;
                case InventarioView:
                    MarcarNav(NavSeccion.Inventario);
                    break;
                case DashboardView:
                    MarcarNav(NavSeccion.Reportes);
                    break;
                case CorteView:
                    MarcarNav(NavSeccion.Corte);
                    break;
                case ConfiguracionView:
                    MarcarNav(NavSeccion.Config);
                    break;
            }
        }

        private void ActualizarEtiquetasPremium()
        {
            var visibilidadPremium = App.LicenseState.OnlineSupport
                ? Visibility.Collapsed
                : Visibility.Visible;

            if (WebPremiumText != null)
                WebPremiumText.Visibility = visibilidadPremium;

            if (SoportePremiumText != null)
                SoportePremiumText.Visibility = visibilidadPremium;
        }

        public void RefrescarEstadoPremium()
        {
            ActualizarEtiquetasPremium();
            ActualizarTiraAlertas();
        }

        private void ActualizarTiraAlertas()
        {
            try
            {
                App.LicenseState.RefreshFromStores();
                if (App.LicenseState.IsBeyondOfflineGrace)
                {
                    LicenseAccessGate.HandleLicenseRevokedDuringSession();
                    return;
                }

                var lines = new List<string>();
                var cfg = new ConfiguracionService();
                var err = cfg.Get("ultimo_backup_nube_error")?.Trim();
                if (App.LicenseState.CloudBackup && !string.IsNullOrEmpty(err))
                    lines.Add("Respaldo en nube: " + err);

                foreach (var diag in LicensingStartupDiagnostics.Run(cfg))
                    lines.Add(diag);

                var pendientes = App.OfflineQueue?.PendingCount() ?? 0;
                if (pendientes > 0)
                    lines.Add($"Ventas/operaciones pendientes de sincronizar: {pendientes} (se reintentan automáticamente).");

                var fallidos = App.OfflineQueue?.FailedPermanentCount() ?? 0;
                if (fallidos > 0)
                    lines.Add($"Operaciones en cola con error permanente: {fallidos} (revise Diagnóstico o soporte).");

                if (App.Connectivity?.State == Services.Connectivity.ConnectivityState.Offline)
                    lines.Add("Sin conexión al servidor. Las ventas se guardan localmente y en cola offline.");

                if (lines.Count == 0)
                {
                    BorderAlertaCaja.Visibility = Visibility.Collapsed;
                    return;
                }

                TxtAlertaCaja.Text = string.Join(Environment.NewLine + Environment.NewLine, lines);
                BorderAlertaCaja.Visibility = Visibility.Visible;
            }
            catch
            {
                BorderAlertaCaja.Visibility = Visibility.Collapsed;
            }
        }

        private void MostrarResumenCobroEnCero()
        {
            if (!_resumenCobroRespaldado)
            {
                _respaldoArticulos = ResumenArticulos.Text;
                _respaldoSubtotal = ResumenSubtotal.Text;
                _respaldoDescuento = ResumenDescuento.Text;
                _respaldoTotal = TotalText.Text;
                _respaldoDetalleLineas = ResumenDetallePanel.Children
                    .OfType<TextBlock>()
                    .Select(t => t.Text)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToArray();
                _resumenCobroRespaldado = true;
            }

            ResumenArticulos.Text = "0";
            ResumenSubtotal.Text = "$0";
            ResumenDescuento.Text = "$0";
            TotalText.Text = "$0";
            ResumenDetallePanel.Children.Clear();
            ResumenDetallePanel.Children.Add(new TextBlock
            {
                Text = "Sin productos",
                Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        private void RestaurarResumenCobroSiCorresponde()
        {
            if (_ventasView != null)
            {
                var lineas = _ventasView.ObtenerLineasActuales();
                if (lineas.Any())
                {
                    int articulos = lineas.Sum(i => i.Cantidad);
                    var subLista = lineas.Sum(i => Math.Round(i.Precio * i.Cantidad, 2, MidpointRounding.AwayFromZero));
                    var neto = lineas.Sum(i => i.Importe);
                    var desc = Math.Round(subLista - neto, 2, MidpointRounding.AwayFromZero);
                    if (desc < 0)
                        desc = 0;
                    VentasView_ResumenActualizado(subLista, desc, neto, articulos);
                    _resumenCobroRespaldado = false;
                    _respaldoDetalleLineas = Array.Empty<string>();
                    return;
                }
            }

            if (!_resumenCobroRespaldado)
                return;

            ResumenArticulos.Text = _respaldoArticulos;
            ResumenSubtotal.Text = _respaldoSubtotal;
            ResumenDescuento.Text = _respaldoDescuento;
            TotalText.Text = _respaldoTotal;

            ResumenDetallePanel.Children.Clear();
            foreach (var linea in _respaldoDetalleLineas)
            {
                ResumenDetallePanel.Children.Add(new TextBlock
                {
                    Text = linea,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 2),
                    Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55))
                });
            }

            if (_respaldoDetalleLineas.Length == 0)
            {
                ResumenDetallePanel.Children.Add(new TextBlock
                {
                    Text = "Sin productos",
                    Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            _resumenCobroRespaldado = false;
            _respaldoDetalleLineas = Array.Empty<string>();
        }

        private void Soporte_Click(object sender, RoutedEventArgs e)
        {
            if (!App.LicenseState.OnlineSupport)
            {
                MessageBox.Show(
                    "El soporte por WhatsApp desde el POS requiere la opción de soporte en línea contratada.\n\n" +
                    "Puede contactar a su proveedor por los canales habituales sin usar esta función.",
                    "Soporte en línea",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            try
            {
                string numero = "56996467784";

                var sesion = App.ObtenerSesionCajaAbiertaVisual();

                string mensaje =
                    $"Usuario: {App.UsuarioActual?.Username}%0A" +
                    $"Caja: {sesion?.NumeroCaja}%0A" +
                    $"Equipo: {Environment.MachineName}%0A" +
                    $"Módulo: {SoporteContexto.Modulo}%0A%0A" +

                    $"Acción que generó el error:%0A{SoporteContexto.Accion}%0A%0A" +

                    $"Detalles:%0A" +
                    $"{(SoporteContexto.Venta > 0 ? $"Venta: {SoporteContexto.Venta:C0}%0A" : "")}" +
                    $"{(!string.IsNullOrEmpty(SoporteContexto.Producto) ? $"Producto: {SoporteContexto.Producto}%0A" : "")}%0A" +

                    $"Error:%0A{SoporteContexto.Error}";

                string url = $"https://web.whatsapp.com/send?phone={numero}&text={mensaje}";

                var ventana = new WebViewWindow(url);

                ventana.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                ventana.Width = 900;
                ventana.Height = 700;
                ventana.Topmost = false;

                ventana.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al abrir soporte:\n" + ex.Message);
            }
        }
    }
}