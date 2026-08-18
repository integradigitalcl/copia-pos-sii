using System;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace GrunflexPOS2.Data
{
    public class AppConfig
    {
        public string ConnectionString { get; set; } = string.Empty;
        public string CajaId { get; set; } = "";
        public string ApiBaseUrl { get; set; } = "http://127.0.0.1:7279/";
        public string PagoApiBaseUrl { get; set; } = "http://127.0.0.1:7279/api/pago";

        /// <summary>Días sin sincronizar con la API antes de mostrar advertencia (modo offline).</summary>
        public int LicensingOfflineGraceDays { get; set; } = 14;

        /// <summary>URL o ruta del feed Velopack para auto-actualizaciones (vacío = deshabilitado).</summary>
        public string UpdateUrl { get; set; } = string.Empty;

        /// <summary>Canal de updates (stable / beta). Reservado para uso futuro de Velopack channels.</summary>
        public string UpdateChannel { get; set; } = "stable";

        /// <summary>Habilita endurecimiento server-side de licencia: registro de terminal + heartbeat.</summary>
        public bool LicenseServerEnforcement { get; set; } = true;

        /// <summary>Habilita endpoints HTTPS en la API local (en addition a HTTP). Requiere cert válido.</summary>
        public bool ApiHttpsEnabled { get; set; } = false;

        /// <summary>
        /// Rol de la terminal en la topología multicaja. Valores:
        ///   - "server": esta PC aloja la BD y la API (caja principal).
        ///   - "client": esta PC es una caja adicional que se conecta al servidor.
        ///   - "" (vacío): no se persistió. Se deduce de la cadena de conexión (UNC = client).
        /// El instalador lo escribe explícito según la opción que eligió el operador, así
        /// la UI no depende de heurísticas frágiles (ej. cadena vacía que cae a local).
        /// </summary>
        public string TerminalRole { get; set; } = string.Empty;

        /// <summary>Usuario SMB en el servidor para conectar el share multicaja (caja adicional).</summary>
        public string SmbShareUser { get; set; } = string.Empty;

        /// <summary>Contraseña del usuario SMB (LAN cerrada; la escribe el instalador).</summary>
        public string SmbSharePassword { get; set; } = string.Empty;

        // --- Multicaja (cliente ↔ API en caja principal; ver docs/MULTICAJA-ELEVENTA-DISENO.md) ---

        /// <summary>Intervalo entre pings a /health/live cuando la API está alcanzable (segundos).</summary>
        public int MulticajaHeartbeatSeconds { get; set; } = 10;

        /// <summary>Intervalo de reintento cuando está offline (segundos).</summary>
        public int MulticajaHeartbeatWhenOfflineSeconds { get; set; } = 3;

        /// <summary>Timeout HTTP del health check (segundos).</summary>
        public int MulticajaHealthTimeoutSeconds { get; set; } = 4;

        /// <summary>Intervalo entre sincronizaciones automáticas de catálogo en caja adicional (segundos).</summary>
        public int MulticajaCatalogSyncIntervalSeconds { get; set; } = 5;

        /// <summary>Si la latencia del health supera este umbral, el estado pasa a Degraded (ms).</summary>
        public int MulticajaDegradedLatencyMs { get; set; } = 1500;

        /// <summary>Si true, cada cambio de conectividad escribe más detalle en diagnósticos.</summary>
        public bool MulticajaVerboseConnectivityLog { get; set; }

        /// <summary>
        /// Reservado: en fases futuras bloqueará ventas/stock críticos cuando no haya API.
        /// Hoy la UI ya depende de <see cref="ConnectivityMonitor"/>; esta bandera amplía reglas.
        /// </summary>
        public bool MulticajaBlockCriticalWhenOffline { get; set; }

        /// <summary>
        /// Caja adicional sin UNC: SQLite sombra local + operaciones de negocio vía API central.
        /// </summary>
        public bool UseMulticajaApiOnlyClient { get; set; }

        /// <summary>
        /// Clave compartida con <c>Multicaja:SharedSecret</c> en la API (cabecera <c>X-Grunflex-Multicaja-Key</c>).
        /// </summary>
        public string MulticajaSharedSecret { get; set; } = string.Empty;

        /// <summary>
        /// Si true y la API no está alcanzable, la venta API-only se guarda en <see cref="Services.Offline.OfflineQueue"/>
        /// para reenvío (idempotente en servidor). Por defecto false: sin red no se registra venta.
        /// </summary>
        public bool MulticajaEnqueueVentaWhenOffline { get; set; }

        /// <summary>Encolar también anulaciones, devoluciones y cierre (además de ventas) cuando no hay API.</summary>
        public bool MulticajaEnqueueWhenOffline { get; set; }

        /// <summary>True si cualquier bandera de encolado offline multicaja está activa.</summary>
        public bool MulticajaEnqueueCriticalWhenOffline => MulticajaEnqueueVentaWhenOffline || MulticajaEnqueueWhenOffline;

        /// <summary>Máximo de intentos de replay por ítem (0 = usar default 32 en cola).</summary>
        public int MulticajaOfflineQueueMaxAttempts { get; set; }

        /// <summary>Mismo error consecutivo: marcar fallo permanente tras N repeticiones.</summary>
        public int MulticajaOfflineQueueSameErrorStreak { get; set; }

        /// <summary>Máximo ítems pendientes en cola offline (0 = sin límite). Evita crecimiento descontrolado.</summary>
        public int MulticajaOfflineQueueMaxPendingItems { get; set; }

        /// <summary>Tamaño total máximo en bytes de archivos <c>*.json</c> en la cola (0 = sin límite).</summary>
        public long MulticajaOfflineQueueMaxTotalBytes { get; set; }

        /// <summary>Si true, el cliente debe tener <see cref="MulticajaSharedSecret"/> (y la API <c>RequireSharedSecret</c>).</summary>
        public bool MulticajaRequireSharedSecret { get; set; }

        /// <summary>
        /// Ruta opcional del JSON de histórico de cortes en esta PC (no central).
        /// Vacío = <c>%LocalAppData%\GrunflexPOS\cortes_historico_local.json</c>.
        /// </summary>
        public string CortesHistoricoLocalPath { get; set; } = "";

        /// <summary>true si la cadena de conexión apunta a un recurso UNC (\\HOST\share).</summary>
        public bool TieneConexionUnc =>
            !string.IsNullOrWhiteSpace(ConnectionString) &&
            (ConnectionString ?? string.Empty).Contains(@"\\", StringComparison.Ordinal);

        /// <summary>
        /// Determinación robusta del rol. Si TerminalRole está persistido lo respetamos;
        /// si no, fallback a la heurística histórica (cadena UNC → client).
        /// </summary>
        public bool EsCajaPrincipal =>
            string.Equals(TerminalRole, "server", StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrWhiteSpace(TerminalRole) && !TieneConexionUnc);

        public bool EsCajaAdicional =>
            string.Equals(TerminalRole, "client", StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrWhiteSpace(TerminalRole) && TieneConexionUnc);

        private const string FileName = "appsettings.local.json";

        /// <summary>
        /// Ruta legacy: junto al .exe. Solo lectura para instalaciones en Program Files.
        /// </summary>
        public static string LegacyLocalPath =>
            Path.Combine(AppContext.BaseDirectory, FileName);

        /// <summary>
        /// Ruta nueva: %LocalAppData%\GrunflexPOS\config\appsettings.local.json
        /// Siempre escribible sin permisos de administrador.
        /// </summary>
        public static string UserLocalPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GrunflexPOS",
                "config",
                FileName);

        /// <summary>
        /// Ruta machine-wide: %ProgramData%\GrunflexPOS\config\appsettings.local.json.
        /// Es la ruta preferida por el instalador (corre elevado y la escribe sin ambigüedad),
        /// y la prioriza al cargar para que la configuración de multicaja sea consistente
        /// para TODOS los usuarios del PC.
        /// </summary>
        public static string MachineLocalPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "GrunflexPOS",
                "config",
                FileName);

        private static int ClampInt(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        private static bool IsSqliteConnectionString(string? cs)
        {
            if (string.IsNullOrWhiteSpace(cs))
                return false;

            var t = cs.Trim();
            return t.StartsWith("Data Source", StringComparison.OrdinalIgnoreCase) ||
                   t.Contains("Data Source=", StringComparison.OrdinalIgnoreCase) ||
                   t.StartsWith("Filename=", StringComparison.OrdinalIgnoreCase);
        }

        public static AppConfig Cargar()
        {
            var basePath = AppContext.BaseDirectory;
            var hasUser = File.Exists(UserLocalPath);
            var hasMachine = File.Exists(MachineLocalPath);

            // Orden de prioridad (la última gana): appsettings.json (base, solo lectura)
            // → legacy local (junto al exe, opcional) → per-user en LocalAppData
            // → machine-wide en ProgramData (la prefiere el instalador, escribible solo
            // por admin, válida para TODOS los usuarios del PC) → variables de entorno.
            var builder = new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddJsonFile(LegacyLocalPath, optional: true, reloadOnChange: false)
                .AddJsonFile(UserLocalPath, optional: true, reloadOnChange: false)
                .AddJsonFile(MachineLocalPath, optional: true, reloadOnChange: false)
                .AddEnvironmentVariables(prefix: "GRUNFLEX_");

            var cfg = builder.Build();

            var rawCs =
                cfg["ConnectionStrings:Default"] ??
                cfg["Database:ConnectionString"] ??
                string.Empty;

            // Auto-saneo UNC: solo rutas \\HOST\... (no tocar C:\ProgramData\...\data\grunflex.db).
            var saneadoCs = SanitizeLegacyUncDataPath(rawCs);
            var fueSaneada = !string.Equals(saneadoCs, rawCs, StringComparison.Ordinal);
            rawCs = saneadoCs;

            var localFix = EnsureLocalProgramDataSqlitePath(rawCs);
            if (!string.Equals(localFix, rawCs, StringComparison.Ordinal))
            {
                rawCs = localFix;
                fueSaneada = true;
            }

            // Si la UNC quedó con barras duplicadas (JSON/escapes viejos), Directory.Exists falla aunque el share exista.
            var normalizedUnc = SqliteConnectionStringHelpers.NormalizeDoubledUncSlashesInSqliteConnectionString(rawCs);
            if (!string.Equals(normalizedUnc, rawCs, StringComparison.Ordinal))
            {
                rawCs = normalizedUnc;
                fueSaneada = true;
            }

            if (string.IsNullOrWhiteSpace(rawCs) || !IsSqliteConnectionString(rawCs))
            {
                // Migración Fase 1: si la BD vive en LocalAppData legacy y ProgramData es escribible,
                // se mueve transparentemente para que la API (como servicio) pueda leerla.
                LocalDatabasePaths.MigrateLegacyToProgramDataIfNeeded();
                LocalDatabasePaths.EnsureDataDirectoryExists();
                rawCs = LocalDatabasePaths.DefaultConnectionString;
                fueSaneada = false;
            }

            var rawApi  = cfg["Api:BaseUrl"]     ?? "http://127.0.0.1:7279/";
            var rawPago = cfg["Api:PagoBaseUrl"] ?? "http://127.0.0.1:7279/api/pago";

            // Auto-saneo extra: si la cadena de conexión apunta por UNC al servidor de
            // multicaja (\\HOST\GrunflexPOS\...) pero la BaseUrl de la API quedó en
            // localhost / 127.0.0.1 (típico residuo de un instalador que escribió la BD
            // bien pero olvidó actualizar el endpoint), reescribimos las URLs usando el
            // mismo host del UNC. Sin esto la sincronización con servidor fallaría con
            // "el equipo de destino denegó la conexión" porque pegamos contra el localhost
            // de la caja adicional (donde no corre la API).
            var serverHost = ExtraerHostUnc(rawCs);
            if (!string.IsNullOrEmpty(serverHost))
            {
                if (EsApiUrlLocal(rawApi))
                {
                    rawApi = $"http://{serverHost}:7279/";
                    fueSaneada = true;
                }
                if (EsApiUrlLocal(rawPago))
                {
                    rawPago = $"http://{serverHost}:7279/api/pago";
                    fueSaneada = true;
                }
            }

            var terminalRoleCfg = (cfg["TerminalRole"] ?? string.Empty).Trim().ToLowerInvariant();
            if (EsApiUrlLocal(rawApi) &&
                (terminalRoleCfg == "client" || PosEdgeRoleProbe.IsPosEdgeTerminal()))
            {
                var hostFix = TryReadPosEdgePersistedServerHost();
                if (!string.IsNullOrWhiteSpace(hostFix))
                {
                    rawApi = $"http://{hostFix}:7279/";
                    rawPago = $"http://{hostFix}:7279/api/pago";
                    fueSaneada = true;
                }
            }

            var config = new AppConfig
            {
                ConnectionString = rawCs.Trim(),
                ApiBaseUrl = rawApi,
                PagoApiBaseUrl = rawPago,
                CajaId = cfg["CajaId"] ?? string.Empty,
                LicensingOfflineGraceDays = cfg.GetValue("Licensing:OfflineGraceDays", 14),
                UpdateUrl = cfg["Updates:Url"] ?? string.Empty,
                UpdateChannel = cfg["Updates:Channel"] ?? "stable",
                LicenseServerEnforcement = cfg.GetValue("Licensing:ServerEnforcement", true),
                ApiHttpsEnabled = cfg.GetValue("Api:HttpsEnabled", false),
                TerminalRole = (cfg["TerminalRole"] ?? string.Empty).Trim().ToLowerInvariant(),
                SmbShareUser = (cfg["SmbShareUser"] ?? string.Empty).Trim(),
                SmbSharePassword = (cfg["SmbSharePassword"] ?? string.Empty).Trim(),
                MulticajaHeartbeatSeconds = ClampInt(cfg.GetValue("Multicaja:HeartbeatSeconds", 10), 2, 120),
                MulticajaHeartbeatWhenOfflineSeconds = ClampInt(cfg.GetValue("Multicaja:HeartbeatWhenOfflineSeconds", 3), 1, 60),
                MulticajaHealthTimeoutSeconds = ClampInt(cfg.GetValue("Multicaja:HealthTimeoutSeconds", 4), 2, 60),
                MulticajaCatalogSyncIntervalSeconds = ClampInt(cfg.GetValue("Multicaja:CatalogSyncIntervalSeconds", 5), 5, 600),
                MulticajaDegradedLatencyMs = ClampInt(cfg.GetValue("Multicaja:DegradedLatencyMs", 1500), 200, 10000),
                MulticajaVerboseConnectivityLog = cfg.GetValue("Multicaja:VerboseConnectivityLog", false),
                MulticajaBlockCriticalWhenOffline = cfg.GetValue("Multicaja:BlockCriticalWhenOffline", false),
                UseMulticajaApiOnlyClient = cfg.GetValue("Multicaja:UseApiOnlyClient", false),
                MulticajaSharedSecret = (cfg["Multicaja:SharedSecret"] ?? string.Empty).Trim(),
                MulticajaEnqueueVentaWhenOffline = cfg.GetValue("Multicaja:EnqueueVentaWhenOffline", true),
                MulticajaEnqueueWhenOffline = cfg.GetValue("Multicaja:EnqueueWhenOffline", true),
                MulticajaOfflineQueueMaxAttempts = ClampInt(cfg.GetValue("Multicaja:OfflineQueueMaxAttempts", 32), 1, 500),
                MulticajaOfflineQueueSameErrorStreak = ClampInt(cfg.GetValue("Multicaja:OfflineQueueSameErrorStreak", 8), 2, 100),
                MulticajaOfflineQueueMaxPendingItems = ClampInt(cfg.GetValue("Multicaja:OfflineQueueMaxPendingItems", 250), 0, 50000),
                MulticajaOfflineQueueMaxTotalBytes = Math.Max(0L, cfg.GetValue("Multicaja:OfflineQueueMaxTotalBytes", 5242880L)),
                MulticajaRequireSharedSecret = cfg.GetValue("Multicaja:RequireSharedSecret", false),
                CortesHistoricoLocalPath = (cfg["Multicaja:CortesHistoricoLocalPath"] ?? string.Empty).Trim()
            };

            if (config.EsCajaAdicional || PosEdgeRoleProbe.IsPosEdgeTerminal())
            {
                config.TerminalRole = "client";
                config.UseMulticajaApiOnlyClient = true;
                config.ConnectionString = LocalDatabasePaths.TerminalClientShadowConnectionString;
            }

            if (!hasUser && !hasMachine)
                config.Guardar();
            else if (fueSaneada)
                config.GuardarReescribiendoCadena();

            return config;
        }

        /// <summary>
        /// Extrae el host (IP o nombre) de una cadena de conexión SQLite con UNC.
        /// Ejemplo: "Data Source=\\\\192.168.1.10\\GrunflexPOS\\grunflex.db;..." → "192.168.1.10"
        /// Devuelve cadena vacía si no es UNC o no se puede parsear.
        /// </summary>
        private static string? TryReadPosEdgePersistedServerHost()
        {
            try
            {
                var posEdgeCfg = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "PosEdge", "config");
                foreach (var name in new[] { "configured-server-host.txt", "install-server-host.txt" })
                {
                    var p = Path.Combine(posEdgeCfg, name);
                    if (!File.Exists(p)) continue;
                    var h = File.ReadAllText(p).Trim();
                    if (!string.IsNullOrWhiteSpace(h) &&
                        !h.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                        !h.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                        return h;
                }

                var cached = Path.Combine(posEdgeCfg, "cached-server.txt");
                if (File.Exists(cached))
                {
                    var url = File.ReadAllText(cached).Trim();
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                        !uri.IsLoopback && !string.IsNullOrWhiteSpace(uri.Host))
                        return uri.Host;
                }
            }
            catch { /* noop */ }
            return null;
        }

        internal static string ExtraerHostUnc(string? cs)
        {
            if (string.IsNullOrWhiteSpace(cs)) return string.Empty;
            var idx = cs.IndexOf(@"\\", StringComparison.Ordinal);
            if (idx < 0) return string.Empty;
            var resto = cs.Substring(idx + 2);
            // Cortar en la primera barra para quedarnos con el host.
            var slash = resto.IndexOf('\\');
            if (slash <= 0) return string.Empty;
            return resto.Substring(0, slash).Trim();
        }

        /// <summary>
        /// Detecta si la URL de la API apunta a la misma máquina (no sirve para multicaja
        /// cuando el POS es una caja adicional). Cubre las variantes habituales: localhost,
        /// 127.0.0.1, ::1.
        /// </summary>
        private static bool EsApiUrlLocal(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return true;
            var u = url.Trim().ToLowerInvariant();
            return u.Contains("//localhost") ||
                   u.Contains("//127.0.0.1") ||
                   u.Contains("//[::1]") ||
                   u.Contains("//::1");
        }

        /// <summary>
        /// Elimina el segmento "\data\" intermedio solo en rutas UNC SMB (multicaja legacy).
        /// "\\HOST\GrunflexPOS\data\grunflex.db" → "\\HOST\GrunflexPOS\grunflex.db".
        /// </summary>
        internal static string SanitizeLegacyUncDataPath(string? cs)
        {
            if (string.IsNullOrWhiteSpace(cs)) return cs ?? string.Empty;
            if (!cs.Contains(@"\\", StringComparison.Ordinal))
                return cs;

            var idx = cs.IndexOf(@"\GrunflexPOS\data\", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return cs;
            var antes = cs.Substring(0, idx + @"\GrunflexPOS".Length);
            var despues = cs.Substring(idx + @"\GrunflexPOS\data".Length);
            return antes + despues;
        }

        /// <summary>
        /// Caja principal local: la BD vive en %ProgramData%\GrunflexPOS\data\grunflex.db.
        /// Instaladores viejos o un saneo UNC mal aplicado dejaban ...\GrunflexPOS\grunflex.db (sin data).
        /// </summary>
        internal static string EnsureLocalProgramDataSqlitePath(string? cs)
        {
            if (string.IsNullOrWhiteSpace(cs)) return cs ?? string.Empty;
            if (cs.Contains(@"\\", StringComparison.Ordinal))
                return cs;

            const string wrong = @"\GrunflexPOS\grunflex.db";
            const string right = @"\GrunflexPOS\data\grunflex.db";
            var idx = cs.IndexOf(wrong, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return cs;
            var tail = idx + wrong.Length;
            if (tail < cs.Length && cs[tail] != ';' && cs[tail] != '"' && !char.IsWhiteSpace(cs[tail]))
                return cs;

            return cs.Substring(0, idx) + right + cs.Substring(tail);
        }

        /// <summary>
        /// Reescribe el appsettings.local.json preservando todo, pero forzando la cadena
        /// saneada (URLs locales reemplazadas por el host UNC, segmento \data\ removido,
        /// etc.). Escribe en AMBAS ubicaciones (Machine y User) para que coincidan: si
        /// solo se escribiera en UserLocalPath, la próxima carga seguiría leyendo el
        /// MachineLocalPath obsoleto (porque tiene prioridad) y el bug volvería.
        /// </summary>
        private void GuardarReescribiendoCadena()
        {
            var csEscaped = ConnectionString.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var apiEscaped = ApiBaseUrl.Replace("\\", "\\\\");
            var pagoEscaped = PagoApiBaseUrl.Replace("\\", "\\\\");
            var cajaIdEscaped = (CajaId ?? string.Empty).Replace("\"", "\\\"");
            var rolEscaped = (TerminalRole ?? string.Empty).Replace("\"", "\\\"");
            var smbUserEscaped = (SmbShareUser ?? string.Empty).Replace("\"", "\\\"");
            var smbPwdEscaped = (SmbSharePassword ?? string.Empty).Replace("\"", "\\\"");

            var json =
                "{\n" +
                "  \"ConnectionStrings\": { \"Default\": \"" + csEscaped + "\" },\n" +
                "  \"Api\": {\n" +
                "    \"BaseUrl\": \"" + apiEscaped + "\",\n" +
                "    \"PagoBaseUrl\": \"" + pagoEscaped + "\"\n" +
                "  },\n" +
                "  \"CajaId\": \"" + cajaIdEscaped + "\",\n" +
                "  \"TerminalRole\": \"" + rolEscaped + "\",\n" +
                "  \"SmbShareUser\": \"" + smbUserEscaped + "\",\n" +
                "  \"SmbSharePassword\": \"" + smbPwdEscaped + "\"\n" +
                "}\n";

            EscribirSeguro(MachineLocalPath, json);
            EscribirSeguro(UserLocalPath, json);
        }

        /// <summary>
        /// Guarda el archivo de configuración por defecto en ambas ubicaciones
        /// (%ProgramData% y %LocalAppData%). El POS y el instalador prefieren
        /// %ProgramData% pero por compatibilidad histórica mantenemos también la
        /// copia de usuario para no romper instalaciones legacy.
        /// </summary>
        public void Guardar()
        {
            var json =
                """
                {
                  "ConnectionStrings": {
                    "Default": ""
                  },
                  "Api": {
                    "BaseUrl": "http://127.0.0.1:7279/",
                    "PagoBaseUrl": "http://127.0.0.1:7279/api/pago"
                  },
                  "CajaId": "",
                  "TerminalRole": "",
                  "SmbShareUser": "",
                  "SmbSharePassword": ""
                }
                """.ReplaceLineEndings();

            EscribirSeguro(MachineLocalPath, json);
            EscribirSeguro(UserLocalPath, json);
        }

        private static void EscribirSeguro(string path, string contenido)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, contenido);
            }
            catch
            {
                // No bloquear arranque si el sistema de archivos no permite escribir
                // (típicamente cuando MachineLocalPath requiere admin y el POS corre
                // como usuario). La otra ubicación cubrirá el caso.
            }
        }
    }
}
