using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Printing;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GrunflexPOS2.Data;
using GrunflexPOS2.Licensing;
using Microsoft.Data.Sqlite;

namespace GrunflexPOS2.Diagnostics;

// =============================================================================
// SqliteCheck: ¿abre la base actual? mide latencia y tamaño.
// =============================================================================
public sealed class SqliteCheck : IDiagnosticCheck
{
    public string Id => "sqlite";
    public string DisplayName => "Base de datos local";
    public string Category => "Sistema";

    public async Task<DiagnosticResult> RunAsync(CancellationToken ct)
    {
        try
        {
            var cfg = AppConfig.Cargar();
            var cs = cfg.ConnectionString;
            if (string.IsNullOrWhiteSpace(cs))
            {
                return new DiagnosticResult
                {
                    Status = DiagnosticStatus.Error,
                    Summary = "No hay cadena de conexión configurada.",
                    TechnicalDetails = "AppConfig.ConnectionString está vacío. Revise appsettings.local.json.",
                    CanAutoFix = true,
                    AutoFix = _ => ResetLocalConfigAsync()
                };
            }

            var sw = Stopwatch.StartNew();
            await using var conn = new SqliteConnection(cs);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA quick_check;";
                var r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                var integrity = r?.ToString() ?? "(null)";
                sw.Stop();

                var path = ExtractDataSource(cs);
                var sizeText = "";
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var size = new FileInfo(path).Length;
                    sizeText = $" ({size / 1024.0 / 1024.0:0.0} MB)";
                }

                if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    return new DiagnosticResult
                    {
                        Status = DiagnosticStatus.Warning,
                        Summary = $"BD accesible pero quick_check devolvió: {integrity}",
                        TechnicalDetails = $"CS: {cs}\nLatencia apertura: {sw.ElapsedMilliseconds} ms"
                    };
                }

                return new DiagnosticResult
                {
                    Status = DiagnosticStatus.Ok,
                    Summary = $"OK — apertura en {sw.ElapsedMilliseconds} ms{sizeText}",
                    TechnicalDetails = $"CS: {cs}"
                };
            }
        }
        catch (Exception ex)
        {
            return new DiagnosticResult
            {
                Status = DiagnosticStatus.Error,
                Summary = "No se puede abrir la base de datos.",
                TechnicalDetails = ex.ToString(),
                CanAutoFix = true,
                AutoFix = _ => ResetLocalConfigAsync()
            };
        }
    }

    private static string ExtractDataSource(string cs)
    {
        var m = Regex.Match(cs, @"Data\s*Source\s*=\s*([^;]+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : string.Empty;
    }

    private static Task<string> ResetLocalConfigAsync()
    {
        try
        {
            var path = AppConfig.UserLocalPath;
            if (File.Exists(path)) File.Delete(path);
            return Task.FromResult("Se restableció appsettings.local.json. Reinicie el POS.");
        }
        catch (Exception ex)
        {
            return Task.FromResult("No se pudo restablecer: " + ex.Message);
        }
    }
}

// =============================================================================
// ApiHealthCheck: /health/live local
// =============================================================================
public sealed class ApiHealthCheck : IDiagnosticCheck
{
    public string Id => "api";
    public string DisplayName => "API local";
    public string Category => "Sistema";

    public async Task<DiagnosticResult> RunAsync(CancellationToken ct)
    {
        try
        {
            var cfg = AppConfig.Cargar();
            var baseUrl = cfg.ApiBaseUrl?.TrimEnd('/') ?? "http://127.0.0.1:7279";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var sw = Stopwatch.StartNew();
            using var resp = await http.GetAsync(baseUrl + "/health/live", ct).ConfigureAwait(false);
            sw.Stop();
            if (resp.IsSuccessStatusCode)
            {
                return new DiagnosticResult
                {
                    Status = DiagnosticStatus.Ok,
                    Summary = $"API responde en {sw.ElapsedMilliseconds} ms",
                    TechnicalDetails = $"GET {baseUrl}/health/live -> {(int)resp.StatusCode}"
                };
            }
            return new DiagnosticResult
            {
                Status = DiagnosticStatus.Warning,
                Summary = $"API respondió HTTP {(int)resp.StatusCode}",
                TechnicalDetails = $"GET {baseUrl}/health/live -> {(int)resp.StatusCode}"
            };
        }
        catch (Exception ex)
        {
            return new DiagnosticResult
            {
                Status = DiagnosticStatus.Error,
                Summary = "API no responde. Probablemente el servicio Windows está detenido.",
                TechnicalDetails = ex.Message,
                CanAutoFix = true,
                AutoFix = _ => RestartApiServiceAsync()
            };
        }
    }

    private static Task<string> RestartApiServiceAsync()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return Task.FromResult("Operación solo válida en Windows.");
#pragma warning disable CA1416
            using var sc = new ServiceController("GrunflexPOSAPI");
            try { _ = sc.Status; }
            catch { return Task.FromResult("El servicio GrunflexPOSAPI no está instalado. Reinstale el POS o ejecute habilitar-recurso-grunflexpos.ps1."); }

            if (sc.Status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            return Task.FromResult("Servicio GrunflexPOSAPI reiniciado.");
#pragma warning restore CA1416
        }
        catch (Exception ex)
        {
            return Task.FromResult("No se pudo reiniciar el servicio: " + ex.Message + ". Pruebe como administrador.");
        }
    }
}

// =============================================================================
// ApiServiceCheck: estado del servicio Windows
// =============================================================================
public sealed class ApiServiceCheck : IDiagnosticCheck
{
    public string Id => "service";
    public string DisplayName => "Servicio Windows GrunflexPOSAPI";
    public string Category => "Sistema";

    public Task<DiagnosticResult> RunAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new DiagnosticResult { Status = DiagnosticStatus.Skipped, Summary = "Solo Windows." });

        try
        {
#pragma warning disable CA1416
            using var sc = new ServiceController("GrunflexPOSAPI");
            ServiceControllerStatus status;
            try { status = sc.Status; }
            catch
            {
                return Task.FromResult(new DiagnosticResult
                {
                    Status = DiagnosticStatus.Error,
                    Summary = "Servicio no instalado. Se inició la API manualmente, lo cual no es recomendable.",
                    TechnicalDetails = "Falta registro del servicio GrunflexPOSAPI. Ejecute como admin: " +
                                       "{app}\\ServerExtras\\habilitar-recurso-grunflexpos.ps1"
                });
            }

            var startTypeText = "";
            try
            {
                using var search = new System.Management.ManagementObjectSearcher(
                    $"SELECT StartMode FROM Win32_Service WHERE Name='GrunflexPOSAPI'");
                foreach (System.Management.ManagementObject mo in search.Get())
                {
                    startTypeText = " (StartMode: " + mo["StartMode"] + ")";
                    break;
                }
            } catch { }

            var (st, summary, autofix) = status switch
            {
                ServiceControllerStatus.Running       => (DiagnosticStatus.Ok, "Servicio ejecutándose", (Func<CancellationToken, Task<string>>?)null),
                ServiceControllerStatus.Stopped       => (DiagnosticStatus.Error, "Servicio detenido", _ => StartServiceAsync()),
                ServiceControllerStatus.StartPending  => (DiagnosticStatus.Warning, "Servicio iniciando...", null),
                ServiceControllerStatus.StopPending   => (DiagnosticStatus.Warning, "Servicio deteniéndose...", null),
                _                                     => (DiagnosticStatus.Warning, $"Estado: {status}", _ => StartServiceAsync())
            };

            return Task.FromResult(new DiagnosticResult
            {
                Status = st,
                Summary = summary + startTypeText,
                TechnicalDetails = $"ServiceName=GrunflexPOSAPI  Status={status}{startTypeText}",
                CanAutoFix = autofix is not null,
                AutoFix = autofix
            });
#pragma warning restore CA1416
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DiagnosticResult
            {
                Status = DiagnosticStatus.Error,
                Summary = "Error consultando el servicio.",
                TechnicalDetails = ex.ToString()
            });
        }
    }

    private static Task<string> StartServiceAsync()
    {
        try
        {
#pragma warning disable CA1416
            using var sc = new ServiceController("GrunflexPOSAPI");
            if (sc.Status != ServiceControllerStatus.Running)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            }
            return Task.FromResult("Servicio iniciado.");
#pragma warning restore CA1416
        }
        catch (Exception ex)
        {
            return Task.FromResult("No se pudo iniciar: " + ex.Message + " (ejecute el POS como administrador).");
        }
    }
}

// =============================================================================
// SmbCheck: si la BD apunta a UNC, prueba acceso al share.
// =============================================================================
public sealed class SmbCheck : IDiagnosticCheck
{
    public string Id => "smb";
    public string DisplayName => "Recurso compartido multicaja (SMB)";
    public string Category => "Multicaja";

    public Task<DiagnosticResult> RunAsync(CancellationToken ct)
    {
        try
        {
            var cs = AppConfig.Cargar().ConnectionString ?? string.Empty;
            var ds = Regex.Match(cs, @"Data\s*Source\s*=\s*([^;]+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            if (!ds.StartsWith(@"\\"))
            {
                return Task.FromResult(new DiagnosticResult
                {
                    Status = DiagnosticStatus.Skipped,
                    Summary = "No aplica (base local; multicaja no está usándose en esta caja).",
                    TechnicalDetails = $"Data Source = {ds}"
                });
            }

            // Acceso al directorio padre del archivo UNC
            var dir = Path.GetDirectoryName(ds);
            var ok = !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
            if (ok && !string.IsNullOrEmpty(dir))
            {
                var files = Directory.GetFiles(dir!).Length;
                return Task.FromResult(new DiagnosticResult
                {
                    Status = DiagnosticStatus.Ok,
                    Summary = $"Share accesible ({files} archivos)",
                    TechnicalDetails = $"UNC: {dir}"
                });
            }
            return Task.FromResult(new DiagnosticResult
            {
                Status = DiagnosticStatus.Error,
                Summary = "No se puede acceder al recurso compartido.",
                TechnicalDetails = $"UNC inaccesible: {dir}\nVerifique: caja principal encendida, ip alcanzable, share creado, credenciales válidas."
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DiagnosticResult
            {
                Status = DiagnosticStatus.Error,
                Summary = "Error verificando SMB.",
                TechnicalDetails = ex.ToString()
            });
        }
    }
}

// =============================================================================
// LanCheck: ping al servidor (solo cliente multicaja).
// =============================================================================
public sealed class LanCheck : IDiagnosticCheck
{
    public string Id => "lan";
    public string DisplayName => "Conectividad LAN";
    public string Category => "Multicaja";

    public async Task<DiagnosticResult> RunAsync(CancellationToken ct)
    {
        var cs = AppConfig.Cargar().ConnectionString ?? string.Empty;
        var ds = Regex.Match(cs, @"Data\s*Source\s*=\s*([^;]+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        if (!ds.StartsWith(@"\\"))
            return new DiagnosticResult { Status = DiagnosticStatus.Skipped, Summary = "No aplica (sin servidor remoto)." };

        var host = ds.Substring(2).Split('\\')[0];
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 1500).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success)
            {
                return new DiagnosticResult
                {
                    Status = DiagnosticStatus.Ok,
                    Summary = $"Ping a {host}: {reply.RoundtripTime} ms",
                    TechnicalDetails = $"Host: {host}, RTT: {reply.RoundtripTime} ms"
                };
            }
            return new DiagnosticResult
            {
                Status = DiagnosticStatus.Error,
                Summary = $"No hay ping a {host} ({reply.Status})",
                TechnicalDetails = $"Host: {host}, Status: {reply.Status}"
            };
        }
        catch (Exception ex)
        {
            return new DiagnosticResult
            {
                Status = DiagnosticStatus.Error,
                Summary = "Error de red: " + ex.Message,
                TechnicalDetails = ex.ToString()
            };
        }
    }
}

// =============================================================================
// FirewallCheck: existe la regla TCP 7279
// =============================================================================
public sealed class FirewallCheck : IDiagnosticCheck
{
    public string Id => "firewall";
    public string DisplayName => "Firewall (puerto 7279)";
    public string Category => "Sistema";

    public Task<DiagnosticResult> RunAsync(CancellationToken ct)
    {
        try
        {
            // Acepta entrada O salida según rol; verificamos ambas.
            var output = RunNetsh("advfirewall firewall show rule name=\"Grunflex POS API (7279)\"");
            var hasInbound = output.Contains("Grunflex POS API (7279)");

            var outboundOut = RunNetsh("advfirewall firewall show rule name=\"Grunflex POS API (out)\"");
            var hasOutbound = outboundOut.Contains("Grunflex POS API (out)");

            if (hasInbound || hasOutbound)
            {
                return Task.FromResult(new DiagnosticResult
                {
                    Status = DiagnosticStatus.Ok,
                    Summary = "Regla de firewall presente.",
                    TechnicalDetails = $"In={hasInbound} Out={hasOutbound}"
                });
            }
            return Task.FromResult(new DiagnosticResult
            {
                Status = DiagnosticStatus.Warning,
                Summary = "No se encontró ninguna regla de firewall del POS.",
                TechnicalDetails = "Si la API es local, no es crítico. Si es multicaja, abrir TCP 7279.",
                CanAutoFix = true,
                AutoFix = _ => CreateFirewallRuleAsync()
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DiagnosticResult
            {
                Status = DiagnosticStatus.Warning,
                Summary = "No se pudo consultar el firewall.",
                TechnicalDetails = ex.ToString()
            });
        }
    }

    private static string RunNetsh(string args)
    {
        try
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo("netsh", args)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            p.Start();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return output ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    private static Task<string> CreateFirewallRuleAsync()
    {
        try
        {
            RunNetsh("advfirewall firewall add rule name=\"Grunflex POS API (7279)\" dir=in action=allow protocol=TCP localport=7279 profile=any");
            return Task.FromResult("Regla creada (entrante TCP 7279). Si esta es una caja cliente, repita con dirección saliente.");
        }
        catch (Exception ex)
        {
            return Task.FromResult("No se pudo crear la regla: " + ex.Message + " (requiere administrador).");
        }
    }
}

// =============================================================================
// PrinterCheck: enumera impresoras y verifica la default.
// =============================================================================
public sealed class PrinterCheck : IDiagnosticCheck
{
    public string Id => "printer";
    public string DisplayName => "Impresora de tickets";
    public string Category => "Periféricos";

    public Task<DiagnosticResult> RunAsync(CancellationToken ct)
    {
        try
        {
            var server = new LocalPrintServer();
            var queues = server.GetPrintQueues();
            var list = queues.Select(q => $"- {q.Name} (online={q.IsOffline == false})").ToArray();
            if (list.Length == 0)
            {
                return Task.FromResult(new DiagnosticResult
                {
                    Status = DiagnosticStatus.Warning,
                    Summary = "No hay impresoras instaladas.",
                    TechnicalDetails = "No se detectaron colas de impresión."
                });
            }
            var defaultQueue = LocalPrintServer.GetDefaultPrintQueue();
            var online = !defaultQueue.IsOffline;
            return Task.FromResult(new DiagnosticResult
            {
                Status = online ? DiagnosticStatus.Ok : DiagnosticStatus.Warning,
                Summary = online
                    ? $"Predeterminada: {defaultQueue.Name}"
                    : $"Predeterminada offline: {defaultQueue.Name}",
                TechnicalDetails = "Impresoras detectadas:\n" + string.Join("\n", list)
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DiagnosticResult
            {
                Status = DiagnosticStatus.Warning,
                Summary = "No se pudieron enumerar impresoras.",
                TechnicalDetails = ex.Message
            });
        }
    }
}

// =============================================================================
// WebView2Check: runtime instalado (registry)
// =============================================================================
public sealed class WebView2Check : IDiagnosticCheck
{
    public string Id => "webview2";
    public string DisplayName => "Microsoft WebView2 Runtime";
    public string Category => "Sistema";

    public Task<DiagnosticResult> RunAsync(CancellationToken ct)
    {
        try
        {
            string?[] paths =
            {
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
                @"HKEY_CURRENT_USER\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
            };
            string? version = null;
            foreach (var p in paths)
            {
                version = Microsoft.Win32.Registry.GetValue(p!, "pv", null) as string;
                if (!string.IsNullOrWhiteSpace(version)) break;
            }
            if (string.IsNullOrWhiteSpace(version))
            {
                return Task.FromResult(new DiagnosticResult
                {
                    Status = DiagnosticStatus.Warning,
                    Summary = "WebView2 no detectado. Las vistas web no funcionarán.",
                    TechnicalDetails = "Reinstalar el POS o ejecutar MicrosoftEdgeWebview2Setup.exe."
                });
            }
            return Task.FromResult(new DiagnosticResult
            {
                Status = DiagnosticStatus.Ok,
                Summary = $"Versión {version}",
                TechnicalDetails = version
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DiagnosticResult { Status = DiagnosticStatus.Warning, Summary = "Error consultando WebView2.", TechnicalDetails = ex.ToString() });
        }
    }
}

// =============================================================================
// LicenseCheck
// =============================================================================
public sealed class LicenseCheck : IDiagnosticCheck
{
    public string Id => "license";
    public string DisplayName => "Licencia";
    public string Category => "Licencia";

    public Task<DiagnosticResult> RunAsync(CancellationToken ct)
    {
        try
        {
            var state = new LicenseStateProvider();
            state.RefreshFromStores();

            if (!state.IsLicensed)
            {
                return Task.FromResult(new DiagnosticResult
                {
                    Status = DiagnosticStatus.Warning,
                    Summary = "No hay módulos pagados activos (modo gratuito).",
                    TechnicalDetails = "Para activar Multicaja u otras funciones premium: Configuración → Activar licencia."
                });
            }

            var exp = state.ExpiresUtc;
            var modules = new List<string>();
            if (state.Multicaja) modules.Add("Multicaja");
            if (state.OnlineSupport) modules.Add("SoporteOnline");
            if (state.CloudBackup) modules.Add("CloudBackup");
            if (state.PrioritySupport) modules.Add("SoportePrioritario");
            var modulesText = modules.Count > 0 ? string.Join(", ", modules) : "(ninguno)";

            if (exp == null)
            {
                return Task.FromResult(new DiagnosticResult
                {
                    Status = DiagnosticStatus.Warning,
                    Summary = "Licencia activa pero sin fecha de expiración registrada.",
                    TechnicalDetails = $"Módulos: {modulesText}\nActivationId: {state.ActivationId ?? "(sin id)"}"
                });
            }

            var days = (exp.Value - DateTime.UtcNow).TotalDays;
            if (days < 0)
            {
                return Task.FromResult(new DiagnosticResult
                {
                    Status = DiagnosticStatus.Error,
                    Summary = $"Licencia VENCIDA hace {-days:0} días.",
                    TechnicalDetails = $"Exp UTC: {exp:o}\nMódulos: {modulesText}"
                });
            }
            if (days < 7)
            {
                return Task.FromResult(new DiagnosticResult
                {
                    Status = DiagnosticStatus.Warning,
                    Summary = $"Activa, vence en {days:0.0} días.",
                    TechnicalDetails = $"Exp UTC: {exp:o}\nMódulos: {modulesText}"
                });
            }
            if (state.IsBeyondOfflineGrace)
            {
                return Task.FromResult(new DiagnosticResult
                {
                    Status = DiagnosticStatus.Warning,
                    Summary = "Hace tiempo que no se valida en línea (excede grace offline).",
                    TechnicalDetails = $"LastCloudOk: {state.LastCloudOkUtc:o}\nGraceDays: {state.OfflineGraceDays}",
                    CanAutoFix = true,
                    AutoFix = _ => SincronizarAhoraAsync()
                });
            }
            if (state.LastCloudOkUtc == null)
            {
                return Task.FromResult(new DiagnosticResult
                {
                    Status = DiagnosticStatus.Warning,
                    Summary = $"Activa pero NUNCA sincronizada con servidor. Vence {exp:dd/MM/yyyy}.",
                    TechnicalDetails = $"Exp UTC: {exp:o}\nMódulos: {modulesText}\nActivationId: {state.ActivationId ?? "(sin id)"}",
                    CanAutoFix = true,
                    AutoFix = _ => SincronizarAhoraAsync()
                });
            }
            return Task.FromResult(new DiagnosticResult
            {
                Status = DiagnosticStatus.Ok,
                Summary = $"Activa. Vence {exp:dd/MM/yyyy}. Módulos: {modulesText}",
                TechnicalDetails = $"Exp UTC: {exp:o}\nLastCloudOk: {state.LastCloudOkUtc:o}\nGraceDays: {state.OfflineGraceDays}",
                CanAutoFix = true,
                AutoFix = _ => SincronizarAhoraAsync()
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DiagnosticResult { Status = DiagnosticStatus.Warning, Summary = "No se pudo evaluar la licencia.", TechnicalDetails = ex.ToString() });
        }
    }

    private static async Task<string> SincronizarAhoraAsync()
    {
        try
        {
            var svc = GrunflexPOS2.App.LicenseSync;
            if (svc != null)
            {
                var ok = await svc.TryRefreshAsync().ConfigureAwait(false);
                return ok ? "Licencia sincronizada con servidor."
                          : "No se pudo sincronizar: " + (svc.LastMessage ?? "sin detalle");
            }
            var (ok2, msg) = await GrunflexPOS2.Services.API.LicensingCloudService.TryRefreshAsync(silent: false).ConfigureAwait(false);
            return ok2 ? "Licencia sincronizada con servidor." : ("No se pudo sincronizar: " + msg);
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }
}

// =============================================================================
// InternetCheck
// =============================================================================
public sealed class InternetCheck : IDiagnosticCheck
{
    public string Id => "internet";
    public string DisplayName => "Internet";
    public string Category => "Red";

    public async Task<DiagnosticResult> RunAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var resp = await http.GetAsync("https://www.cloudflare.com/cdn-cgi/trace", ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                return new DiagnosticResult { Status = DiagnosticStatus.Ok, Summary = "Internet disponible.", TechnicalDetails = $"HTTP {(int)resp.StatusCode}" };
            }
            return new DiagnosticResult { Status = DiagnosticStatus.Warning, Summary = $"Respuesta inesperada (HTTP {(int)resp.StatusCode})." };
        }
        catch
        {
            return new DiagnosticResult { Status = DiagnosticStatus.Warning, Summary = "Sin conexión a Internet. Las funciones nube quedan limitadas." };
        }
    }
}

// =============================================================================
// TelemetryCheck: muestra contadores de eventos clave
// =============================================================================
public sealed class TelemetryCheck : IDiagnosticCheck
{
    public string Id => "telemetry";
    public string DisplayName => "Eventos y métricas";
    public string Category => "Sistema";

    public Task<DiagnosticResult> RunAsync(CancellationToken ct)
    {
        try
        {
            var snap = GrunflexPOS2.Services.Telemetry.Telemetry.Snapshot();
            if (snap.Count == 0)
                return Task.FromResult(new DiagnosticResult { Status = DiagnosticStatus.Ok, Summary = "Sin eventos registrados." });

            // Marcamos warning si hay crashes o errores acumulados
            long crashes = 0;
            snap.TryGetValue("app.crash", out crashes);
            long apiErrors = 0;
            snap.TryGetValue("api.error", out apiErrors);

            var status = (crashes > 0) ? DiagnosticStatus.Warning
                       : (apiErrors > 5) ? DiagnosticStatus.Warning
                       : DiagnosticStatus.Ok;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{snap.Count} contadores activos:");
            foreach (var kv in snap.OrderByDescending(k => k.Value).Take(20))
                sb.AppendLine($"  {kv.Key}: {kv.Value}");
            sb.AppendLine();
            sb.AppendLine("Últimos 10 eventos:");
            foreach (var e in GrunflexPOS2.Services.Telemetry.Telemetry.RecentEvents().Reverse().Take(10))
                sb.AppendLine($"  {e.Timestamp.ToLocalTime():HH:mm:ss}  {e.Name}");

            var summary = $"app.crash={crashes}, api.error={apiErrors}, total {snap.Count} eventos.";
            return Task.FromResult(new DiagnosticResult
            {
                Status = status,
                Summary = summary,
                TechnicalDetails = sb.ToString()
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DiagnosticResult { Status = DiagnosticStatus.Warning, Summary = "Error.", TechnicalDetails = ex.Message });
        }
    }
}

// =============================================================================
// TerminalRegistrationCheck: estado del registro server-side
// =============================================================================
public sealed class TerminalRegistrationCheck : IDiagnosticCheck
{
    public string Id => "terminal-registration";
    public string DisplayName => "Registro de caja en servidor";
    public string Category => "Licencia";

    public Task<DiagnosticResult> RunAsync(CancellationToken ct)
    {
        var c = GrunflexPOS2.App.TerminalRegistration;
        if (c == null)
            return Task.FromResult(new DiagnosticResult { Status = DiagnosticStatus.Skipped, Summary = "Registro no inicializado." });

        if (c.SlotsTotal == 0)
            return Task.FromResult(new DiagnosticResult { Status = DiagnosticStatus.Warning, Summary = "Sin respuesta del servidor de licencia (se reintentará).", TechnicalDetails = c.LastReason ?? "" });

        if (!c.LastGranted)
            return Task.FromResult(new DiagnosticResult
            {
                Status = DiagnosticStatus.Error,
                Summary = $"Slot DENEGADO ({c.SlotsInUse}/{c.SlotsTotal} usados).",
                TechnicalDetails = c.LastReason ?? "Compre licencia con más cajas o libere una desde administración."
            });

        return Task.FromResult(new DiagnosticResult
        {
            Status = DiagnosticStatus.Ok,
            Summary = $"Caja registrada. Slots {c.SlotsInUse}/{c.SlotsTotal}.",
            TechnicalDetails = $"Fingerprint: {GrunflexPOS2.Services.Licensing.TerminalRegistrationClient.MachineFingerprint()}"
        });
    }
}

// =============================================================================
// UpdatesCheck: estado del updater (Velopack)
// =============================================================================
public sealed class UpdatesCheck : IDiagnosticCheck
{
    public string Id => "updates";
    public string DisplayName => "Actualizaciones";
    public string Category => "Sistema";

    public Task<DiagnosticResult> RunAsync(CancellationToken ct)
    {
        var up = GrunflexPOS2.App.Updater;
        if (up == null)
            return Task.FromResult(new DiagnosticResult { Status = DiagnosticStatus.Skipped, Summary = "Updater no inicializado." });

        if (!up.Enabled)
            return Task.FromResult(new DiagnosticResult { Status = DiagnosticStatus.Skipped, Summary = "Updates deshabilitados (Updates:Url vacío)." });

        return up.State switch
        {
            GrunflexPOS2.Services.Updates.UpdateState.UpToDate =>
                Task.FromResult(new DiagnosticResult { Status = DiagnosticStatus.Ok, Summary = $"Versión actual: {up.CurrentVersion ?? "?"} (al día)." }),
            GrunflexPOS2.Services.Updates.UpdateState.UpdateAvailable =>
                Task.FromResult(new DiagnosticResult { Status = DiagnosticStatus.Warning, Summary = $"Nueva versión disponible: {up.AvailableVersion}", TechnicalDetails = "Use el botón 'Buscar actualizaciones' en Configuración para descargar." }),
            GrunflexPOS2.Services.Updates.UpdateState.ReadyToApply =>
                Task.FromResult(new DiagnosticResult { Status = DiagnosticStatus.Ok, Summary = "Actualización descargada. Reinicie el POS para aplicar." }),
            GrunflexPOS2.Services.Updates.UpdateState.Error =>
                Task.FromResult(new DiagnosticResult { Status = DiagnosticStatus.Warning, Summary = "Error consultando updates.", TechnicalDetails = up.LastError ?? "" }),
            _ =>
                Task.FromResult(new DiagnosticResult { Status = DiagnosticStatus.Ok, Summary = $"Estado: {up.State}" })
        };
    }
}

// =============================================================================
// OfflineQueueCheck: cuantos items pendientes hay en la cola offline
// =============================================================================
public sealed class OfflineQueueCheck : IDiagnosticCheck
{
    public string Id => "offline-queue";
    public string DisplayName => "Cola offline";
    public string Category => "Multicaja";

    public Task<DiagnosticResult> RunAsync(CancellationToken ct)
    {
        try
        {
            var q = GrunflexPOS2.App.OfflineQueue;
            if (q == null)
                return Task.FromResult(new DiagnosticResult { Status = DiagnosticStatus.Skipped, Summary = "Cola no inicializada." });

            var items = q.ListItems();
            var pending = items.Where(i => !i.Done).ToList();
            if (pending.Count == 0)
                return Task.FromResult(new DiagnosticResult { Status = DiagnosticStatus.Ok, Summary = "Sin operaciones pendientes." });

            var withErrors = pending.Where(i => !string.IsNullOrEmpty(i.LastError)).ToList();
            var status = withErrors.Count > 0 ? DiagnosticStatus.Warning : DiagnosticStatus.Ok;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Pendientes: {pending.Count}");
            foreach (var i in pending.Take(8))
                sb.AppendLine($"- {i.Kind} id={i.Id[..8]} intentos={i.AttemptCount} err={i.LastError ?? "-"}");
            return Task.FromResult(new DiagnosticResult
            {
                Status = status,
                Summary = $"{pending.Count} pendientes" + (withErrors.Count > 0 ? $" ({withErrors.Count} con error)" : ""),
                TechnicalDetails = sb.ToString()
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DiagnosticResult { Status = DiagnosticStatus.Warning, Summary = "Error consultando cola.", TechnicalDetails = ex.Message });
        }
    }
}

// =============================================================================
// BackupsCheck: existe alguna copia reciente en el directorio de backups
// =============================================================================
public sealed class BackupsCheck : IDiagnosticCheck
{
    public string Id => "backups";
    public string DisplayName => "Backups recientes";
    public string Category => "Datos";

    public Task<DiagnosticResult> RunAsync(CancellationToken ct)
    {
        try
        {
            var backups = (GrunflexPOS2.App.Backups?.ListBackups()) ?? new List<GrunflexPOS2.Services.Backups.BackupInfo>();
            if (backups.Count == 0)
            {
                return Task.FromResult(new DiagnosticResult
                {
                    Status = DiagnosticStatus.Warning,
                    Summary = "No se detectan snapshots automáticos. Riesgo de pérdida ante corrupción.",
                    TechnicalDetails = $"Directorio: {GrunflexPOS2.Services.Backups.BackupService.BackupsDirectory}",
                    CanAutoFix = true,
                    AutoFix = _ => Task.FromResult(CrearSnapshotManual())
                });
            }

            var latest = backups.First();
            var age = DateTime.UtcNow - latest.CreatedUtc;
            var ageText = age.TotalDays < 1 ? $"{age.TotalHours:0.0} h" : $"{age.TotalDays:0.0} días";
            var status = age.TotalDays > 2 ? DiagnosticStatus.Warning : DiagnosticStatus.Ok;
            return Task.FromResult(new DiagnosticResult
            {
                Status = status,
                Summary = $"Último: {latest.FileName} ({ageText} atrás, {latest.SizeDisplay})",
                TechnicalDetails = "Snapshots:\n" + string.Join("\n", backups.Take(8).Select(b => $"- {b.FileName} ({b.CreatedDisplay}, {b.SizeDisplay})"))
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DiagnosticResult { Status = DiagnosticStatus.Warning, Summary = "Error listando snapshots.", TechnicalDetails = ex.Message });
        }
    }

    private static string CrearSnapshotManual()
    {
        try
        {
            var info = GrunflexPOS2.App.Backups?.CreateManualBackup();
            return info != null ? $"Snapshot creado: {info.FileName}" : "BackupService no disponible.";
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }
}
