using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using GrunflexPOS2.Data;
using GrunflexPOS2.Domain.Abstractions;
using GrunflexPOS2.Infrastructure.Setup;
using GrunflexPOS2.Infrastructure.Session;
using GrunflexPOS2.Licensing;
using Grunflex.Licensing.Security;
using GrunflexPOS2.Models.Entities;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Backups;
using GrunflexPOS2.Services.Connectivity;
using GrunflexPOS2.Services.Offline;
using GrunflexPOS2.Services.Telemetry;
using GrunflexPOS2.Services.Licensing;
using GrunflexPOS2.Services.Updates;
using GrunflexPOS2.Networking;
using GrunflexPOS2.Services.Multicaja;
using GrunflexPOS2.Services.Multicaja.Bootstrap;
using GrunflexPOS2.Services.Multicaja.Sync;
using GrunflexPOS2.Services.Multicaja.Terminal;
using GrunflexPOS2.Startup;
using GrunflexPOS2.Views;
using QuestPDF.Infrastructure;

namespace GrunflexPOS2
{
    public partial class App : System.Windows.Application
    {
        private static readonly UserSessionContext Session = new();
        public static IUserSessionContext SessionContext => Session;

        /// <summary>Lectura unificada de plan / módulos según licencia aplicada.</summary>
        public static ILicenseStateProvider LicenseState { get; } = new LicenseStateProvider();

        public static Usuario? UsuarioActual { get => Session.UsuarioActual; set => Session.UsuarioActual = value; }
        public static Guid CajaActualId { get => Session.CajaActualId; set => Session.CajaActualId = value; }

        public static GrunflexDbContext DbContext { get; set; } = null!;
        public static VentaService VentaService { get; set; } = null!;

        /// <summary>Sesión de caja abierta en el servidor (multicaja API-only). Null si no aplica.</summary>
        public static CajaSesion? MulticajaSesionEnServidor { get; set; }

        public static CajaSesion? ObtenerSesionCajaAbiertaVisual() =>
            MulticajaSesionEnServidor ?? DbContext.CajaSesiones.FirstOrDefault(c => c.Abierta);

        public static void PersistirCajaTerminalId(Guid cajaId) => PersistirCajaIdEnUserConfig(cajaId, AppConfig.Cargar());

        /// <summary>Búsqueda de productos en catálogo local (sin API para operar).</summary>
        public static IProductoLookupService ProductoLookup { get; private set; } = null!;

        /// <summary>Monitor de conectividad con la API; arranca al iniciar y reporta cambios de estado.</summary>
        public static ConnectivityMonitor Connectivity { get; private set; } = null!;

        /// <summary>Servicio de respaldos automáticos de la BD SQLite (singleton).</summary>
        public static BackupService Backups { get; private set; } = null!;

        /// <summary>Cola persistente de operaciones offline (Fase 3.2). Disponible global; los servicios la usan al detectar caída.</summary>
        public static OfflineQueue OfflineQueue { get; private set; } = null!;

        /// <summary>Servicio de actualizaciones in-app (Velopack, Fase 4).</summary>
        public static UpdaterService Updater { get; private set; } = null!;

        /// <summary>Cliente de registro de terminal contra la API local (Fase 5.1).</summary>
        public static TerminalRegistrationClient? TerminalRegistration { get; private set; }

        /// <summary>Identidad y heartbeat enterprise de terminal.</summary>
        public static TerminalService? Terminal { get; private set; }

        /// <summary>Sync periódico y reconexión multicaja (caja adicional API-only).</summary>
        public static MulticajaBackgroundServices? MulticajaBackground =>
            MulticajaRealtime?.Background;

        /// <summary>Invalidación realtime, event bus y sync por IDs.</summary>
        public static MulticajaRealtimeServices? MulticajaRealtime { get; private set; }

        /// <summary>Sincronizador automático de licencia con la API (startup + periódico + reconexión).</summary>
        public static GrunflexPOS2.Services.Licensing.LicenseSyncService? LicenseSync { get; private set; }

        /// <summary>Módulo a abrir tras login (accesos directos del instalador).</summary>
        public static string? PendingStartupModule { get; set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // ⚠️ Velopack: PRIMERO de todo, antes incluso de base.OnStartup, para que pueda
            // procesar argumentos --veloapp-* del setup/uninstall sin levantar la UI.
            UpdaterService.InitializeStartup();

            PendingStartupModule = StartupNavigation.ParseModule(e.Args);

            if (EsModoInstaladorConectarServidor(e.Args))
            {
                base.OnStartup(e);
                var cultInst = new CultureInfo("es-CL");
                CultureInfo.DefaultThreadCurrentCulture = cultInst;
                CultureInfo.DefaultThreadCurrentUICulture = cultInst;
                try
                {
                    var dlg = new ConectarServidorWindow { ModoInstalador = true };
                    dlg.ShowDialog();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "No se pudo completar el paso de conexión a la caja principal:\n\n" + ex.Message,
                        "Grunflex POS — instalación",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }

                Shutdown();
                return;
            }

            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Fase 6: telemetría de arranque
            try
            {
                var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
                Telemetry.Track("app.started", new Dictionary<string, object?>
                {
                    ["version"] = ver,
                    ["machine"] = Environment.MachineName,
                    ["os"] = Environment.OSVersion.VersionString
                });
            }
            catch { }

            // Capturamos cualquier excepción no manejada para que el POS nunca muera silencioso.
            this.DispatcherUnhandledException += (s, ex) =>
            {
                try { PosDiagnostics.Log("UI no manejada", ex.Exception); } catch { }
                try
                {
                    Telemetry.Track("app.crash", new Dictionary<string, object?>
                    {
                        ["scope"] = "ui",
                        ["type"] = ex.Exception?.GetType().FullName,
                        ["message"] = ex.Exception?.Message
                    });
                } catch { }
                var detail = ex.Exception?.GetBaseException()?.Message ?? ex.Exception?.Message ?? "(sin detalle)";
                MessageBox.Show(
                    "Ocurrió un error inesperado:\n\n" + detail +
                    "\n\nDetalle técnico en %LocalAppData%\\GrunflexPOS\\logs\\pos.log",
                    "Grunflex POS",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                ex.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                try { PosDiagnostics.Log("Dominio no manejada", ex.ExceptionObject as Exception); } catch { }
            };

            var cl = new CultureInfo("es-CL");
            CultureInfo.DefaultThreadCurrentCulture = cl;
            CultureInfo.DefaultThreadCurrentUICulture = cl;

            QuestPDF.Settings.License = LicenseType.Community;

            TerminalConfigBootstrap.ApplyIfNeeded();

            var cfgApi = AppConfig.Cargar();
            MulticajaRuntime.UseApiOnlyClient =
                cfgApi.UseMulticajaApiOnlyClient || cfgApi.EsCajaAdicional;
            if (MulticajaRuntime.UseApiOnlyClient)
            {
                if (!TryResolverCajaIdTerminalApi(cfgApi))
                {
                    MessageBox.Show(
                        "No se pudo asignar la caja a este equipo.\n\n" +
                        "Opciones:\n" +
                        "  1) Copie grunflex-terminal.json del escritorio de la caja principal a este PC (Escritorio o carpeta del programa) y vuelva a abrir.\n" +
                        "  2) En la caja principal: Cajas → Conectar más cajas → cree la caja y genere la plantilla.\n" +
                        "  3) Verifique que la API de la principal esté encendida y accesible en la IP configurada.",
                        "Grunflex POS — CajaId",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown();
                    return;
                }

                cfgApi = AppConfig.Cargar();
                var configErr = TerminalBootstrapOrchestrator.ValidateClientConfig(cfgApi);
                if (configErr != null)
                {
                    MessageBox.Show(configErr, "Grunflex POS — configuración multicaja",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown();
                    return;
                }

                LocalDatabasePaths.EnsureTerminalShadowParentExists();
                if (cfgApi.MulticajaRequireSharedSecret &&
                    string.IsNullOrWhiteSpace(cfgApi.MulticajaSharedSecret))
                    PosDiagnostics.Log(
                        "multicaja.auth: RequireSharedSecret está activo pero Multicaja:SharedSecret está vacío; las llamadas a API fallarán.");
            }

            // Multicaja legacy SMB: omitir en modo API-only.
            if (cfgApi.TieneConexionUnc && !MulticajaRuntime.UseApiOnlyClient)
            {
                var smbOk = SmbShareConnectionBootstrap.TryEnsureShareForSqlite(cfgApi);
                if (!smbOk)
                    PosDiagnostics.Log("SMB: no se confirmó el recurso de red antes de validar SQLite (detalle en este log).");
            }

            // Si la cadena de conexión apunta a una ruta de red inaccesible, el POS no puede
            // iniciar como terminal: ofrecemos al usuario restablecer a SQLite local en este PC.
            if (!PuedeAbrirSqlite(cfgApi.ConnectionString, out var motivoBd))
            {
                // En cajas ADICIONALES, ofrecer "usar BD local" es una trampa: te aleja del
                // problema real (no se ve el share del servidor) y termina con dos cajas
                // desincronizadas. Detectamos el caso y damos un mensaje accionable distinto.
                bool esIntentoMulticaja = !string.IsNullOrEmpty(cfgApi.ConnectionString)
                                          && cfgApi.ConnectionString.Contains(@"\\", StringComparison.Ordinal);

                if (esIntentoMulticaja)
                {
                    var ipServ = AppConfig.ExtraerHostUnc(cfgApi.ConnectionString);
                    MessageBox.Show(
                        "Esta PC está configurada como CAJA ADICIONAL pero no puede ver el recurso compartido del servidor.\n\n" +
                        motivoBd + "\n\n" +
                        $"Servidor (host): {ipServ}\n" +
                        $"Cadena: {cfgApi.ConnectionString}\n\n" +
                        "El POS intenta conectar solo al recurso SMB con el usuario 'grunflexshare' (sin scripts obligatorios).\n\n" +
                        "Comprobaciones:\n" +
                        "  1) La caja principal encendida y en la misma red.\n" +
                        "  2) En el servidor existe el recurso \\\\…\\\\GrunflexPOS (reinstalá como «Caja principal» si hace falta).\n" +
                        "  3) Usuario y contraseña del share coinciden con la instalación (por defecto GrunflexLan2025SMB).\n\n" +
                        "Si querés usar esta PC como caja PRINCIPAL (BD local, sin servidor), tocá «Sí» en el siguiente mensaje.",
                        "Grunflex POS - sin conexión con la caja principal",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);

                    var resp2 = MessageBox.Show(
                        "¿Convertir esta PC en caja PRINCIPAL ahora? (usará la BD local de este equipo y se desconectará del servidor)",
                        "Grunflex POS",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (resp2 != MessageBoxResult.Yes)
                    {
                        Shutdown();
                        return;
                    }
                }
                else
                {
                    var resp = MessageBox.Show(
                        "No se pudo abrir la base de datos configurada para este equipo.\n\n" +
                        motivoBd + "\n\n" +
                        "Cadena de conexión:\n" + cfgApi.ConnectionString + "\n\n" +
                        "¿Desea restablecer la configuración para que el POS use la base local de este PC?\n" +
                        "(Esto desconectará este equipo de cualquier caja remota previamente configurada.)",
                        "Grunflex POS - configuración de terminal",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (resp != MessageBoxResult.Yes)
                    {
                        Shutdown();
                        return;
                    }
                }

                if (!RestablecerTerminalLocal(out var msgReset))
                {
                    MessageBox.Show(
                        "No se pudo restablecer la configuración: " + msgReset,
                        "Grunflex POS",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown();
                    return;
                }

                cfgApi = AppConfig.Cargar();
            }

            // Enriquecer la cadena de conexión: ante locks transitorios en SQLite sobre SMB
            // (especialmente cuando la API del servidor está escribiendo), reintentar hasta
            // 10 segundos antes de fallar. Sin esto cualquier conflicto efímero rompe el arranque.
            var connStr = EnsureSqliteBusyTimeout(cfgApi.ConnectionString, milliseconds: 10000);

            var options = new DbContextOptionsBuilder<GrunflexDbContext>()
                .UseSqlite(connStr)
                .AddInterceptors(new SqliteBusyTimeoutInterceptor(millis: 10000))
                .Options;

            DbContext = new GrunflexDbContext(options);

            try
            {
                SqliteSchemaBootstrap.EnsureMigrated(DbContext, cfgApi.TieneConexionUnc);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "No se pudo preparar la base de datos local antes de continuar:\n\n" +
                    ex.GetBaseException().Message,
                    "Grunflex POS",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
                return;
            }

            // ProductoLookup se setea más abajo, una vez que existe el ConnectivityMonitor.
            ProductoLookup = new ProductoLocalLookupService(DbContext);

            var init = new InicializadorService(DbContext);
            init.Inicializar();

            // Auto-registro plug-and-play: si el equipo está conectado a una base central
            // compartida y aún no tiene CajaId, crea automáticamente un registro de Caja
            // con el nombre del equipo y guarda el GUID en la configuración local.
            if (AutoRegistrarCajaSiCorresponde(cfgApi, out var nuevoCajaId))
            {
                cfgApi.CajaId = nuevoCajaId.ToString();
            }

            if (!ValidarCajaTerminal(cfgApi))
            {
                Shutdown();
                return;
            }

            if (!ValidarConectividadCentral(cfgApi))
            {
                Shutdown();
                return;
            }

            VentaService = new VentaService(DbContext, SessionContext);

            // Connectivity monitor: arranca en background, reacciona a cambios para que
            // la UI (status indicator) y otros servicios sepan si la API esta online.
            Connectivity = new ConnectivityMonitor(() => AppConfig.Cargar().ApiBaseUrl);
            try
            {
                var mc = cfgApi;
                Connectivity.ApplyMulticajaSettings(
                    TimeSpan.FromSeconds(mc.MulticajaHeartbeatSeconds),
                    TimeSpan.FromSeconds(mc.MulticajaHeartbeatWhenOfflineSeconds),
                    TimeSpan.FromSeconds(mc.MulticajaHealthTimeoutSeconds),
                    TimeSpan.FromMilliseconds(mc.MulticajaDegradedLatencyMs));
            }
            catch (Exception ex) { PosDiagnostics.Log("ConnectivityMonitor ApplyMulticajaSettings", ex); }

            try
            {
                MulticajaStartupValidation.Run(cfgApi, Connectivity);
            }
            catch (Exception ex) { PosDiagnostics.Log("MulticajaStartupValidation", ex); }

            Connectivity.StateChanged += (_, state) =>
            {
                try
                {
                    var verbose = false;
                    try { verbose = AppConfig.Cargar().MulticajaVerboseConnectivityLog; } catch { }

                    if (verbose)
                    {
                        PosDiagnostics.Log(
                            $"multicaja.connect state={state} latencyMs={Connectivity.LastLatencyMs} " +
                            $"lastCheckUtc={Connectivity.LastCheckUtc:O} urlBase={AppConfig.Cargar().ApiBaseUrl} err={Connectivity.LastError ?? "ok"}");
                    }
                    else
                        PosDiagnostics.Log($"Conectividad: {state} (latencia {Connectivity.LastLatencyMs}ms, last={Connectivity.LastError ?? "ok"})");
                }
                catch { }
                try
                {
                    Telemetry.Track("connectivity.changed", new Dictionary<string, object?>
                    {
                        ["state"] = state.ToString(),
                        ["latencyMs"] = Connectivity.LastLatencyMs,
                        ["error"] = Connectivity.LastError
                    });
                } catch { }
                try
                {
                    if (MulticajaRuntime.UseApiOnlyClient)
                        MulticajaRiskScanner.Scan($"connectivity_{state}");
                }
                catch { }
            };
            Connectivity.Start();

            // ProductoLookup resiliente (Fase 2.4): elige dinámicamente entre API y SQLite,
            // y hace fallback a SQLite si la API cae. Drop-in compatible.
            ProductoLookup = new GrunflexPOS2.Services.DataSources.ResilientProductoLookupService(DbContext, Connectivity);

            // Fase 3.1: scheduler de backups automáticos cada 6h con retención GFS.
            // Sólo en caja principal (BD local). En cliente multicaja no sirve duplicar.
            Backups = new BackupService();
            try
            {
                var csBackup = cfgApi.ConnectionString ?? string.Empty;
                if (!cfgApi.UseMulticajaApiOnlyClient && !csBackup.Contains(@"\\", StringComparison.Ordinal))
                    Backups.StartScheduler();
            }
            catch (Exception ex) { PosDiagnostics.Log("BackupService no pudo iniciar", ex); }

            // Fase 3.2: cola offline persistente para operaciones que requieren conectividad.
            // Los handlers concretos se registran por feature (ej. envío de venta).
            try
            {
                OfflineQueue = new OfflineQueue(Connectivity);
                OfflineQueue.RegisterHandler("multicaja-venta-commit", new MulticajaVentaCommitOfflineHandler());
                OfflineQueue.RegisterHandler("multicaja-anular-venta", new MulticajaAnulacionOfflineHandler());
                OfflineQueue.RegisterHandler("multicaja-devolucion-linea", new MulticajaDevolucionOfflineHandler());
                OfflineQueue.RegisterHandler("multicaja-cierre-sesion", new MulticajaCierreOfflineHandler());
                OfflineQueue.RegisterHandler("multicaja-movimiento-caja", new MulticajaMovimientoCajaOfflineHandler());
                OfflineQueue.Start();
                try
                {
                    if (MulticajaRuntime.UseApiOnlyClient)
                        MulticajaRiskScanner.Scan("offline_queue_ready");
                }
                catch { }
            }
            catch (Exception ex) { PosDiagnostics.Log("OfflineQueue no pudo iniciar", ex); }

            // Fase 4: chequeo de actualizaciones in-app (best-effort, sin bloquear UI).
            try
            {
                Updater = new UpdaterService();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30));
                        await Updater.CheckAsync();
                    }
                    catch (Exception ex) { PosDiagnostics.Log("Updater chequeo inicial falló", ex); }
                });
            }
            catch (Exception ex) { PosDiagnostics.Log("UpdaterService no pudo iniciar", ex); }

            // Auto-sync de licencia con la API: arranca a los 10s, repite cada 12h y al
            // reconectar. Llena el campo "Última sincronización OK con API" del panel de
            // licencia y refresca ProductEntitlements si el servidor cambió algo.
            try
            {
                LicenseSync = new GrunflexPOS2.Services.Licensing.LicenseSyncService(Connectivity);
                LicenseSync.Refreshed += (_, _) =>
                {
                    try
                    {
                        Dispatcher.Invoke(() =>
                        {
                            App.LicenseState.RefreshFromStores();
                            if (!LicenseAccessGate.IsPosAccessAllowed())
                                LicenseAccessGate.HandleLicenseRevokedDuringSession();
                        });
                    }
                    catch { }
                };
                LicenseSync.Start();
            }
            catch (Exception ex) { PosDiagnostics.Log("LicenseSyncService no pudo iniciar", ex); }

            // Registro de terminal: solo cajas adicionales (evita 127.0.0.1 en panel del servidor).
            if (cfgApi.EsCajaAdicional && MulticajaRuntime.UseApiOnlyClient)
            {
                try
                {
                    var trHttp = new HttpClient { BaseAddress = new Uri(cfgApi.ApiBaseUrl), Timeout = TimeSpan.FromSeconds(12) };
                    TerminalRegistration = new TerminalRegistrationClient(trHttp);
                    Terminal = new TerminalService(trHttp);
                }
                catch (Exception ex) { PosDiagnostics.Log("TerminalRegistration no pudo crearse", ex); }
            }

            ConfiguracionService? cfgStartup = null;
            try
            {
                cfgStartup = new ConfiguracionService();
                LectorCodigoService.IniciarDesdeConfiguracion(cfgStartup);
                LicenseService.TryApplyStoredLicense(cfgStartup, out _);
            }
            catch
            {
                // no romper inicio por lector serial
            }

            cfgStartup ??= new ConfiguracionService();
            try
            {
                MigrarContrasenasLegacySiCorresponde();
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log("MigrarContrasenasLegacy", ex);
            }

            if (cfgApi.EsCajaPrincipal)
                GrunflexDataDirectoryAcl.TryRepairCommerceDatabase();

            if (!CompletaPrimeraInstalacionSiCorresponde(cfgStartup))
            {
                Shutdown();
                return;
            }

            foreach (var issue in LicensingStartupDiagnostics.Run(cfgStartup))
                PosDiagnostics.Log("licencia.startup: " + issue);

            if (MulticajaRuntime.UseApiOnlyClient)
            {
                App.LicenseState.RefreshFromStores();
                if (!App.LicenseState.Multicaja)
                {
                    MessageBox.Show(
                        LicenseAccessGate.MensajeMulticajaRequerida,
                        "Licencia Multicaja",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    Shutdown();
                    return;
                }

                // Bootstrap usa coordinador mínimo; el realtime completo arranca después.
                var bootstrapCaches = new Services.Multicaja.Cache.MulticajaCacheRegistry();
                var bootstrapBus = new Services.Multicaja.Events.LocalEventBus();
                var bootstrapDelta = new DeltaSyncService(
                    new HttpClient
                    {
                        BaseAddress = new Uri(cfgApi.ApiBaseUrl),
                        Timeout = TimeSpan.FromSeconds(12)
                    },
                    () => Connectivity.State);
                var syncCoord = new MulticajaSyncCoordinator(
                    bootstrapDelta,
                    bootstrapCaches,
                    bootstrapBus,
                    () => Connectivity.State);
                var orchestrator = new TerminalBootstrapOrchestrator(syncCoord, Terminal, TerminalRegistration);
                var bootstrapDlg = new TerminalBootstrapWindow(orchestrator);
                bootstrapDlg.ShowDialog();
                if (!bootstrapDlg.Success)
                {
                    MessageBox.Show(
                        string.IsNullOrWhiteSpace(bootstrapDlg.FailureMessage)
                            ? "No se pudo preparar la terminal multicaja."
                            : bootstrapDlg.FailureMessage,
                        "Grunflex POS — terminal",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown();
                    return;
                }
            }

            var login = new LoginWindow();
            Current.MainWindow = login;
            login.Show();
            login.Activate();

            if (MulticajaRuntime.UseApiOnlyClient && App.LicenseState.Multicaja)
            {
                try
                {
                    var trHttp = new HttpClient
                    {
                        BaseAddress = new Uri(AppConfig.Cargar().ApiBaseUrl),
                        Timeout = TimeSpan.FromSeconds(12)
                    };
                    MulticajaRealtime = new MulticajaRealtimeServices(
                        Connectivity, trHttp, Terminal, TerminalRegistration);
                    MulticajaRealtime.Start(AppConfig.Cargar());
                }
                catch (Exception ex) { PosDiagnostics.Log("MulticajaRealtimeServices", ex); }
            }
        }

        private static bool ValidarCajaTerminal(AppConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(cfg.CajaId))
                return true;

            if (!Guid.TryParse(cfg.CajaId, out var cajaId))
            {
                MessageBox.Show(
                    "La configuración de terminal es inválida (CajaId no es GUID válido).\n" +
                    "Vuelva a generar y aplicar la plantilla de multicaja.",
                    "Configuración de terminal",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (cfg.UseMulticajaApiOnlyClient && cfg.EsCajaAdicional)
            {
                try
                {
                    if (Task.Run(() => MulticajaOperacionesClient.CajaExisteAsync(cajaId)).GetAwaiter().GetResult())
                        return true;
                }
                catch (Exception ex)
                {
                    PosDiagnostics.Log("Validar caja vía API", ex);
                }

                MessageBox.Show(
                    "Este terminal no encuentra la caja en el servidor (API).\n" +
                    "Revise Api:BaseUrl, la red y que la caja principal esté encendida.",
                    "Caja no encontrada",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            var existe = DbContext.Cajas.Any(c => c.Id == cajaId);
            if (existe)
                return true;

            MessageBox.Show(
                "Este terminal está asociado a una caja que no existe en la base central.\n" +
                "Revise la plantilla de multicaja o vuelva a conectar la caja desde el servidor.",
                "Caja no encontrada",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        private static bool ValidarConectividadCentral(AppConfig cfg)
        {
            // Solo exigir conectividad de red para terminales multicaja (cuando hay CajaId asignado).
            if (string.IsNullOrWhiteSpace(cfg.CajaId))
                return true;

            if (!(cfg.UseMulticajaApiOnlyClient && cfg.EsCajaAdicional))
            {
                if (!ValidarAccesoRutaSqliteCentral(cfg.ConnectionString, out var errorRuta))
                {
                    MessageBox.Show(
                        "No se pudo acceder a la base central compartida.\n\n" +
                        errorRuta + "\n\n" +
                        "Revise que en la caja principal esté habilitado el recurso compartido " +
                        "(script: habilitar-recurso-grunflexpos.ps1).",
                        "Conexión multicaja",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }
            }

            if (!ValidarApiCentral(cfg.ApiBaseUrl, out var errorApi))
            {
                MessageBox.Show(
                    "No se pudo conectar a la API del servidor principal.\n\n" +
                    errorApi + "\n\n" +
                    "Verifique que la API esté levantada en la caja principal y accesible desde esta terminal.",
                    "Conexión API",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private static bool ValidarAccesoRutaSqliteCentral(string connectionString, out string error)
        {
            error = string.Empty;
            try
            {
                var dataSource = ExtraerDataSourceSqlite(connectionString);
                if (string.IsNullOrWhiteSpace(dataSource))
                {
                    error = "La cadena de conexión no contiene Data Source.";
                    return false;
                }

                if (!dataSource.StartsWith(@"\\", StringComparison.Ordinal))
                    return true; // local: no validar share de red

                var dir = Path.GetDirectoryName(dataSource) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    error = $"No se encontró la carpeta compartida: {dir}";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool ValidarApiCentral(string apiBaseUrl, out string error)
        {
            error = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(apiBaseUrl))
                {
                    error = "Api:BaseUrl está vacío.";
                    return false;
                }

                var probe = apiBaseUrl.TrimEnd('/') + "/health/live";
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using var response = http.GetAsync(probe).GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode)
                    return true;

                error = $"Estado HTTP {(int)response.StatusCode} al consultar {probe}";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Antes de validar multicaja: importa plantilla (si existe) o auto-registra la caja en el servidor.
        /// </summary>
        private static bool TryResolverCajaIdTerminalApi(AppConfig cfg)
        {
            if (!cfg.EsCajaAdicional && !MulticajaRuntime.UseApiOnlyClient)
                return true;

            if (Guid.TryParse((cfg.CajaId ?? "").Trim(), out var existente) && existente != Guid.Empty)
                return true;

            TerminalConfigBootstrap.ApplyIfNeeded();
            cfg = AppConfig.Cargar();
            if (Guid.TryParse((cfg.CajaId ?? "").Trim(), out existente) && existente != Guid.Empty)
                return true;

            return AutoRegistrarCajaSoloApi(cfg, out _);
        }

        private static bool AutoRegistrarCajaSoloApi(AppConfig cfg, out Guid cajaId)
        {
            cajaId = Guid.Empty;
            if (!string.IsNullOrWhiteSpace(cfg.CajaId) && Guid.TryParse(cfg.CajaId, out var existente))
            {
                try
                {
                    if (Task.Run(() => MulticajaOperacionesClient.CajaExisteAsync(existente)).GetAwaiter()
                            .GetResult())
                        return false;
                }
                catch
                {
                    return false;
                }

                cfg.CajaId = string.Empty;
                PosDiagnostics.Log("CajaId huérfano (API): se limpia para re-registro automático.");
            }

            try
            {
                var r = Task.Run(() => MulticajaOperacionesClient.AutoRegistroCajaAsync(Environment.MachineName))
                    .GetAwaiter().GetResult();
                if (r == null || !r.Ok)
                    return false;
                cajaId = r.CajaId;
                cfg.CajaId = cajaId.ToString();
                PersistirCajaIdEnUserConfig(cajaId, cfg);
                return true;
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log("Auto-registro de caja (API)", ex);
                return false;
            }
        }

        /// <summary>
        /// Auto-registro plug-and-play: cuando el POS detecta que está apuntando a una base
        /// central compartida (\\servidor\GrunflexPOS) pero aún no tiene CajaId, crea
        /// automáticamente un registro de Caja en esa base y persiste el GUID en la
        /// configuración local del usuario.
        /// </summary>
        private static bool AutoRegistrarCajaSiCorresponde(AppConfig cfg, out Guid cajaId)
        {
            cajaId = Guid.Empty;

            if (cfg.UseMulticajaApiOnlyClient && cfg.EsCajaAdicional)
                return AutoRegistrarCajaSoloApi(cfg, out cajaId);

            // Solo aplica si la BD es central (UNC \\HOST\share); en caja principal no.
            var ds = ExtraerDataSourceSqlite(cfg.ConnectionString);
            if (string.IsNullOrWhiteSpace(ds) || !ds.StartsWith(@"\\", StringComparison.Ordinal))
                return false;

            // Si ya hay CajaId, validar que exista en la BD central. Si NO existe
            // (típico cuando la caja adicional cambió de servidor o el servidor se
            // reinstaló desde cero), tratarlo como "sin CajaId" y registrar una nueva.
            // Esto evita el "Caja no encontrada" que requería intervención manual.
            if (!string.IsNullOrWhiteSpace(cfg.CajaId))
            {
                if (Guid.TryParse(cfg.CajaId, out var existente))
                {
                    try
                    {
                        if (DbContext.Cajas.Any(c => c.Id == existente))
                            return false; // CajaId vigente, no hace falta re-registrar
                    }
                    catch
                    {
                        return false; // si la consulta falla, mejor no tocar nada
                    }
                }
                // CajaId huérfano: lo limpiamos y seguimos al flujo de auto-registro normal.
                cfg.CajaId = string.Empty;
                PosDiagnostics.Log("CajaId huérfano detectado en config local: se limpia y se re-registra automáticamente.");
            }

            try
            {
                if (!DbContext.Empresas.Any())
                    return false;

                var empresa = DbContext.Empresas.First();

                // Estrategia de nombre: una caja por equipo (clave por MachineName).
                // Si ya existe una caja registrada por este MachineName (independiente
                // del nombre histórico, "MyPC - Caja", "Caja 92", etc.), reusamos. Si no,
                // creamos una nueva con nombre secuencial "Caja N" donde N = total + 1.
                var marcadorLegacy = $"{Environment.MachineName} - Caja";
                var sufijoMaquina = $"({Environment.MachineName})";

                var ya = DbContext.Cajas.FirstOrDefault(c =>
                    c.Nombre == marcadorLegacy ||
                    c.Nombre.Contains(sufijoMaquina));

                if (ya != null)
                {
                    cajaId = ya.Id;
                }
                else
                {
                    int siguiente = DbContext.Cajas.Count() + 1;
                    var slotCfg = new ConfiguracionService();
                    if (!GrunflexPOS2.Services.Licensing.CajaSlotGate.CanCreateActiveCaja(DbContext, slotCfg, out var limiteMsg))
                    {
                        PosDiagnostics.Log("Auto-registro de caja bloqueado: " + limiteMsg);
                        return false;
                    }

                    var nombreNuevo = $"Caja {siguiente} {sufijoMaquina}";

                    var caja = new Caja
                    {
                        Id = Guid.NewGuid(),
                        Nombre = nombreNuevo,
                        EmpresaId = empresa.Id,
                        Activa = true,
                        FechaCreacion = DateTime.UtcNow
                    };
                    DbContext.Cajas.Add(caja);
                    DbContext.SaveChanges();
                    cajaId = caja.Id;
                }

                PersistirCajaIdEnUserConfig(cajaId, cfg);
                return true;
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log("Auto-registro de caja", ex);
                return false;
            }
        }

        private static void PersistirCajaIdEnUserConfig(Guid cajaId, AppConfig cfg)
        {
            // Escribir el CajaId en AMBAS ubicaciones para que la próxima carga lo encuentre
            // independientemente del orden de prioridad. Si solo escribimos en LocalAppData,
            // la lectura prefiere MachineLocalPath (ProgramData) y nos quedamos con el
            // CajaId huérfano viejo → loop "Caja no encontrada" sin fin.
            var smbUser = string.IsNullOrWhiteSpace(cfg.SmbShareUser)
                ? MulticajaLanDefaults.ShareUser
                : cfg.SmbShareUser.Trim();
            var smbPwd = string.IsNullOrWhiteSpace(cfg.SmbSharePassword)
                ? MulticajaLanDefaults.SharePassword
                : cfg.SmbSharePassword;

            var doc = new Dictionary<string, object?>
            {
                ["ConnectionStrings"] = new Dictionary<string, string?> { ["Default"] = cfg.ConnectionString },
                ["Api"] = new Dictionary<string, string?>
                {
                    ["BaseUrl"] = cfg.ApiBaseUrl,
                    ["PagoBaseUrl"] = cfg.PagoApiBaseUrl
                },
                ["CajaId"] = cajaId.ToString(),
                ["TerminalRole"] = string.IsNullOrWhiteSpace(cfg.TerminalRole) ? "client" : cfg.TerminalRole,
                ["SmbShareUser"] = smbUser,
                ["SmbSharePassword"] = smbPwd
            };

            var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });

            EscribirConfigSeguro(AppConfig.MachineLocalPath, json);
            EscribirConfigSeguro(AppConfig.UserLocalPath, json);
        }

        private static void EscribirConfigSeguro(string path, string json)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log($"Guardar config en {path}", ex);
            }
        }

        private static bool PuedeAbrirSqlite(string connectionString, out string motivo)
        {
            motivo = string.Empty;
            var dataSource = ExtraerDataSourceSqlite(connectionString);
            if (string.IsNullOrWhiteSpace(dataSource))
            {
                motivo = "La cadena de conexión no contiene Data Source.";
                return false;
            }

            // Sólo validamos rutas de red (UNC \\servidor\recurso). Las rutas locales serán creadas por la migración.
            if (!dataSource.StartsWith(@"\\", StringComparison.Ordinal))
                return true;

            try
            {
                var dir = Path.GetDirectoryName(dataSource);
                if (string.IsNullOrWhiteSpace(dir))
                {
                    motivo = "No se pudo determinar la carpeta del recurso de red.";
                    return false;
                }

                for (var intento = 0; intento < 5; intento++)
                {
                    try
                    {
                        if (File.Exists(dataSource))
                            return true;
                        if (Directory.Exists(dir))
                            return true;
                    }
                    catch
                    {
                        // Reintentar (SMB aún no listo)
                    }

                    Thread.Sleep(400);
                }

                motivo = $"No se encontró la carpeta compartida: {dir}";
                return false;
            }
            catch (Exception ex)
            {
                motivo = ex.Message;
                return false;
            }
        }

        private static bool RestablecerTerminalLocal(out string mensaje)
        {
            mensaje = string.Empty;
            try
            {
                // Escribimos una plantilla vacía en %LocalAppData% (siempre escribible)
                // que prevalece sobre la versión legacy en Program Files. Así el POS
                // pasa a usar SQLite local sin necesitar permisos de administrador.
                var userPath = AppConfig.UserLocalPath;
                var dir = Path.GetDirectoryName(userPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(userPath,
                    """
                    {
                      "ConnectionStrings": { "Default": "" },
                      "Api": {
                        "BaseUrl": "http://127.0.0.1:7279/",
                        "PagoBaseUrl": "http://127.0.0.1:7279/api/pago"
                      },
                      "CajaId": ""
                    }
                    """);

                // Best-effort: borrar también la versión legacy junto al .exe y plantillas viejas.
                try
                {
                    var baseDir = AppContext.BaseDirectory;
                    foreach (var name in new[] { "appsettings.local.json", "grunflex-terminal.json" })
                    {
                        var p = Path.Combine(baseDir, name);
                        if (File.Exists(p))
                        {
                            try { File.Delete(p); } catch { /* sin permisos: ignorar */ }
                        }
                    }
                }
                catch
                {
                    // ignorar
                }

                return true;
            }
            catch (Exception ex)
            {
                mensaje = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Garantiza que la cadena de conexión SQLite incluya un Default Timeout y un
        /// busy_timeout (a través del wrapper Microsoft.Data.Sqlite "Default Timeout" se
        /// aplica como SQLITE_BUSY backoff). En multicaja sobre SMB, esto evita que un
        /// lock transitorio del servidor (mientras escribe la API) rompa el POS.
        /// </summary>
        private static string EnsureSqliteBusyTimeout(string cs, int milliseconds)
        {
            if (string.IsNullOrWhiteSpace(cs)) return cs;
            // "Default Timeout" en Microsoft.Data.Sqlite es en SEGUNDOS. Convertimos.
            var seconds = Math.Max(1, milliseconds / 1000);
            // Si ya tiene "Default Timeout=" lo respetamos.
            if (cs.IndexOf("Default Timeout", StringComparison.OrdinalIgnoreCase) >= 0)
                return cs;
            var sep = cs.TrimEnd().EndsWith(';') ? string.Empty : ";";
            return $"{cs}{sep}Default Timeout={seconds}";
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

        private static void MigrarContrasenasLegacySiCorresponde()
        {
            if (DbContext == null)
                return;

            try
            {
                var usuarios = DbContext.Usuarios
                    .AsNoTracking()
                    .Select(u => new { u.Username, u.Password })
                    .ToList();
                var migrated = 0;
                foreach (var u in usuarios)
                {
                    if (string.IsNullOrEmpty(u.Password) || PasswordHasher.IsBcryptHash(u.Password))
                        continue;

                    var hash = PasswordHasher.Hash(u.Password);
                    DbContext.Database.ExecuteSqlRaw(
                        "UPDATE Usuarios SET Password = {0} WHERE Username = {1}",
                        hash,
                        u.Username);
                    migrated++;
                }

                if (migrated > 0)
                    PosDiagnostics.Log($"App.MigrarContrasenasLegacy: {migrated} contraseña(s) migradas a BCrypt");
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log("App.MigrarContrasenasLegacy", ex);
            }
        }

        /// <summary>
        /// Primera instalación: licencia → crear administrador → login (similar a eleventa).
        /// Instalaciones ya existentes (ya hay usuarios) solo marcan el paso como completado.
        /// </summary>
        private static bool CompletaPrimeraInstalacionSiCorresponde(ConfiguracionService cfg)
        {
            // No usar solo «instalacion_completada»: si quedó en true sin licencia real, saltaba todo el asistente y solo veían login.

            // Si aún no hay licencia válida, mostrar activación aunque ya existan usuarios (bases antiguas con admin sembrado).
            App.LicenseState.RefreshFromStores();
            if (!LicenseAccessGate.IsPosAccessAllowed())
            {
                var status = LicenseService.EvaluateStored(cfg, out _);
                var modo = App.LicenseState.IsBeyondOfflineGrace
                    ? ActivacionLicenciaModo.FueraGraciaOffline
                    : status == LicenseStatus.Expired
                        ? ActivacionLicenciaModo.Expirada
                        : ActivacionLicenciaModo.PrimeraVez;
                var activacion = new ActivacionLicenciaWindow(modo);
                if (activacion.ShowDialog() != true)
                    return false;

                App.LicenseState.RefreshFromStores();
                if (!LicenseAccessGate.IsPosAccessAllowed())
                    return false;
            }

            if (!DbContext.Usuarios.Any())
            {
                var cfg0 = AppConfig.Cargar();
                // Caja adicional: los usuarios viven en el servidor. Con API-only la sombra
                // local puede no tener filas en Usuarios; no debe abrirse CrearUsuarioInicial aquí.
                if (cfg0.EsCajaAdicional)
                {
                    // Caja adicional: usuarios viven en el servidor; ir directo al login.
                }
                else
                {
                    var crearAdmin = new CrearUsuarioInicialWindow();
                    if (crearAdmin.ShowDialog() != true)
                        return false;
                }
            }

            if (cfg.Get("instalacion_completada") != "true")
                cfg.Set("instalacion_completada", "true");

            return true;
        }

        private static bool EsModoInstaladorConectarServidor(string[]? args) =>
            args != null && args.Any(static a =>
                string.Equals(a, "--installer-conectar-servidor", StringComparison.OrdinalIgnoreCase));

        protected override void OnExit(ExitEventArgs e)
        {
            try { Telemetry.Track("app.exited"); } catch { }
            try { LectorCodigoService.Detener(); } catch { }
            try { Connectivity?.Dispose(); } catch { }
            try { Backups?.Dispose(); } catch { }
            try { OfflineQueue?.Dispose(); } catch { }
            try { Terminal?.StopHeartbeat(); } catch { }
            try { MulticajaRealtime?.Dispose(); } catch { }
            try { LicenseSync?.Dispose(); } catch { }
            base.OnExit(e);
        }

        /// <summary>Última sincronización de catálogo/cajeros en caja adicional (evita pull completo en cada re-login).</summary>
        internal static DateTime LastMulticajaCatalogSyncUtc { get; set; } = DateTime.MinValue;

        public static void Logout()
        {
            try
            {
                UsuarioActual = null;
                CajaActualId = Guid.Empty;
                MulticajaSesionEnServidor = null;

                // Mantener servicios en segundo plano: no detener heartbeat, conectividad ni cola offline.
                if (Current.MainWindow != null)
                {
                    if (Current.MainWindow is CajaView cajaView)
                        cajaView.PermitirCerrar();
                    Current.MainWindow.Close();
                }

                var login = new LoginWindow();
                Current.MainWindow = login;
                login.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al cerrar sesión:\n" + ex.Message);
            }
        }
    }
}
