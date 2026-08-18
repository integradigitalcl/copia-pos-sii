using IC = PosEdge.InstallerCore.InstallerCore;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;

namespace PosEdge.Launcher;

internal sealed class LauncherHost
{
    private readonly string _payloadRoot;
    private readonly bool _repair;
    private readonly bool _postInstall;
    private readonly SplashWindow _splash;
    private readonly string? _module;
    private string _grunflexHost = "127.0.0.1";

    public LauncherHost(string payloadRoot, bool repair, bool postInstall, SplashWindow splash, string? module = null)
    {
        _payloadRoot = payloadRoot;
        _repair = repair;
        _postInstall = postInstall;
        _splash = splash;
        _module = module;
    }

    public async Task<int> RunAsync()
    {
        try
        {
            await SetStatusAsync("Preparando sistema...");
            await Task.Delay(200).ConfigureAwait(false);

            if (_repair)
            {
                await SetStatusAsync("Recuperando sistema...");
                await RunSetupAgentHiddenAsync("--repair").ConfigureAwait(false);
            }

            var role = ReadRole();
            if (role != "server" && role != "terminal")
            {
                await SetStatusAsync("Validando instalación...");
                await RunSetupAgentHiddenAsync("").ConfigureAwait(false);
                role = ReadRole();
            }

            if (role != "server" && role != "terminal")
            {
                await ShowErrorAsync("La instalación no está completa. Vuelva a ejecutar el instalador.");
                return 20;
            }

            if (role == "terminal")
            {
                var apiBase = ReadCachedServer();
                _grunflexHost = IC.ExtractHostFromApiUrl(apiBase) ?? "127.0.0.1";
            }

            // Servicios Windows siguen en segundo plano: si la API ya responde, abrir el POS al instante.
            if (!_repair && await ProbeGrunflexApiAsync(_grunflexHost, TimeSpan.FromSeconds(3)).ConfigureAwait(false))
            {
                await SetStatusAsync("Sistema listo");
                await Task.Delay(150).ConfigureAwait(false);
                if (role == "server")
                    await PrepareCommerceDbPermissionsAsync().ConfigureAwait(false);
                await LaunchPosAsync().ConfigureAwait(false);
                return 0;
            }

            if (role == "server")
            {
                var fastPath = _postInstall && !_repair && IC.TryGetServerSecrets() != null;
                await SetStatusAsync("Iniciando servicios...");
                await PrepareServerRuntimeAsync(fullProvision: !fastPath).ConfigureAwait(false);
                await StartServerServicesAsync().ConfigureAwait(false);
            }
            else
            {
                await SetStatusAsync("Conectando servidor...");
                var apiBase = ReadCachedServer();
                await Task.Run(() => IC.EnsureGrunflexClientProvisioned(apiBase)).ConfigureAwait(false);
                _grunflexHost = IC.ExtractHostFromApiUrl(apiBase) ?? "127.0.0.1";
                await StartServicesAsync("PosEdgeGuardian").ConfigureAwait(false);
            }

            if (!await WaitForGrunflexApiAsync(_grunflexHost, TimeSpan.FromMinutes(3)).ConfigureAwait(false))
            {
                await SetStatusAsync("Recuperando servicios...");
                await RunSetupAgentHiddenAsync("--repair").ConfigureAwait(false);
                if (role == "server")
                {
                    await PrepareServerRuntimeAsync(fullProvision: false).ConfigureAwait(false);
                    await StartServerServicesAsync().ConfigureAwait(false);
                }

                if (!await WaitForGrunflexApiAsync(_grunflexHost, TimeSpan.FromMinutes(2)).ConfigureAwait(false))
                {
                    LogStartupDiagnostics("Grunflex API no respondió tras reparación.");
                    var logHint = GetLatestLauncherLogHint();
                    var msg = role == "server"
                        ? $"El servidor local no inició a tiempo.{logHint}\n\nIntente: Menú Inicio → Reparar sistema."
                        : "No se encontró la caja principal en la red. Verifique el cable o Wi‑Fi.";

                    if (role == "server" && await ConfirmOpenPosAnywayAsync())
                    {
                        await LaunchPosAsync().ConfigureAwait(false);
                        return 0;
                    }

                    await ShowErrorAsync(msg);
                    return 20;
                }
            }

            await SetStatusAsync("Sistema listo");
            await Task.Delay(350).ConfigureAwait(false);
            if (role == "server")
                await PrepareCommerceDbPermissionsAsync().ConfigureAwait(false);
            await LaunchPosAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            IC.LogSetupFailure(ex);
            LogLauncher(ex.ToString());
            var logHint = GetLatestLauncherLogHint();
            await ShowErrorAsync($"No se pudo iniciar Grunflex POS.{logHint}");
            return 20;
        }
    }

    private Task PrepareCommerceDbPermissionsAsync() =>
        Task.Run(() => IC.FinalizeGrunflexCommerceDataPermissions(waitForDbMs: 20_000));

    private async Task PrepareServerRuntimeAsync(bool fullProvision)
    {
        _grunflexHost = "127.0.0.1";
        await Task.Run(() =>
        {
            try
            {
                if (fullProvision)
                    IC.EnsureServerProvisioned(_payloadRoot);
                else
                    IC.EnsureServerRuntimeReady(_payloadRoot, postgresWaitSeconds: 60);
            }
            catch (Exception ex) { IC.LogSetupFailure(ex); }

            try
            {
                IC.EnsureGrunflexServerProvisioned(_payloadRoot);
                IC.EnsureFirewallRuleTcp5071();
                IC.EnsureFirewallRuleUdp33279();
                IC.EnsureFirewallRuleUdp5353();
                IC.EnsureFirewallRuleTcp7279();
            }
            catch (Exception ex) { IC.LogSetupFailure(ex); }
        }).ConfigureAwait(false);
    }

    private Task StartServerServicesAsync() =>
        StartServicesSequencedAsync(
            ("PosEdgePostgres", 3000),
            ("GrunflexPOSAPI", 12000),
            ("PosEdgeApi", 2000),
            ("PosEdgeWorkers", 2000),
            ("PosEdgeGuardian", 2000));

    private Task StartServicesSequencedAsync(params (string Name, int DelayMs)[] steps) =>
        Task.Run(async () =>
        {
            foreach (var (name, delayMs) in steps)
            {
                StartService(name);
                if (delayMs > 0)
                    await Task.Delay(delayMs).ConfigureAwait(false);
            }
        });

    private static void StartService(string name)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"start \"{name}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit(8000);
        }
        catch { }
    }

    private Task LaunchPosAsync() =>
        _splash.Dispatcher.InvokeAsync(() =>
        {
            var posExe = Path.Combine(_payloadRoot, "GrunflexPOS", "GrunflexPOS2.exe");
            if (!File.Exists(posExe))
                throw new FileNotFoundException("GrunflexPOS2.exe no encontrado.", posExe);

            Process.Start(new ProcessStartInfo
            {
                FileName = posExe,
                Arguments = string.IsNullOrWhiteSpace(_module) ? "" : $"--module {_module}",
                WorkingDirectory = Path.GetDirectoryName(posExe)!,
                UseShellExecute = true
            });
        }).Task;

    private Task SetStatusAsync(string message) =>
        _splash.Dispatcher.InvokeAsync(() => _splash.SetStatus(message)).Task;

    private Task ShowErrorAsync(string message) =>
        _splash.Dispatcher.InvokeAsync(() =>
            MessageBox.Show(_splash, message, "Grunflex POS", MessageBoxButton.OK, MessageBoxImage.Warning)).Task;

    private Task<bool> ConfirmOpenPosAnywayAsync() =>
        _splash.Dispatcher.InvokeAsync(() =>
            MessageBox.Show(
                _splash,
                "El servidor aún no responde. ¿Desea abrir Grunflex POS de todos modos?\n\nPuede usar Menú Inicio → Reparar sistema.",
                "Grunflex POS",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes).Task;

    private static string GetLatestLauncherLogHint()
    {
        try
        {
            var dir = IC.LogsDir;
            if (!Directory.Exists(dir))
                return "";

            var latest = Directory.GetFiles(dir, "launcher-*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latest == null)
                return "";

            return $"\n\nDetalle técnico guardado en:\n{latest.FullName}";
        }
        catch { return ""; }
    }

    private void LogStartupDiagnostics(string reason)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine(reason);
            sb.AppendLine();
            sb.AppendLine("=== GrunflexPOSAPI (sc query) ===");
            sb.AppendLine(IC.TryQueryServiceState("GrunflexPOSAPI") ?? "(sin datos)");
            sb.AppendLine();
            sb.AppendLine("=== Últimas líneas grunflex-api log ===");
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "GrunflexPOS", "logs");
            sb.AppendLine(IC.TryReadTailOfNewestLog(logDir, "grunflex-api-*.log") ?? "(sin archivo de log)");
            LogLauncher(sb.ToString());
        }
        catch { }
    }

    private static void LogLauncher(string text)
    {
        try
        {
            var dir = IC.LogsDir;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"launcher-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, text, Encoding.UTF8);
        }
        catch { }
    }

    private string ReadRole()
    {
        try
        {
            var p = Path.Combine(IC.ConfigDir, "role.txt");
            if (!File.Exists(p)) return "";
            return File.ReadAllText(p, Encoding.UTF8).Trim().ToLowerInvariant();
        }
        catch { return ""; }
    }

    private string? ReadCachedServer()
    {
        try
        {
            var p = Path.Combine(IC.ConfigDir, "cached-server.txt");
            if (!File.Exists(p)) return null;
            return File.ReadAllText(p, Encoding.UTF8).Trim();
        }
        catch { return null; }
    }

    private Task RunSetupAgentHiddenAsync(string extraArgs)
    {
        var setupAgent = Path.Combine(_payloadRoot, "SetupAgent", "PosEdge.SetupAgent.exe");
        if (!File.Exists(setupAgent))
            return Task.CompletedTask;

        var args = $"--payload-root \"{_payloadRoot}\" {extraArgs}".Trim();
        return Task.Run(() =>
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = setupAgent,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            p?.WaitForExit(5 * 60_000);
        });
    }

    private static Task StartServicesAsync(params string[] names) =>
        Task.Run(() =>
        {
            foreach (var name in names)
                StartService(name);
        });

    private static async Task<bool> ProbeGrunflexApiAsync(string host, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = timeout };
        var baseUrl = $"http://{host}:7279";
        try
        {
            using var resp = await http.GetAsync($"{baseUrl}/health/live").ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
                return true;
        }
        catch { }

        try
        {
            using var resp = await http.GetAsync($"{baseUrl}/health").ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> WaitForGrunflexApiAsync(string host, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + timeout;
        var baseUrl = $"http://{host}:7279";
        var attempt = 0;

        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            if (attempt % 4 == 1)
                await SetStatusAsync("Conectando servidor...").ConfigureAwait(false);
            else if (attempt % 4 == 2)
                await SetStatusAsync("Iniciando servicios...").ConfigureAwait(false);
            else if (attempt % 4 == 3)
                await SetStatusAsync("Base de datos lista...").ConfigureAwait(false);
            else
                await SetStatusAsync("Reconectando servidor...").ConfigureAwait(false);

            try
            {
                using var resp = await http.GetAsync($"{baseUrl}/health/live").ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return true;
            }
            catch { }

            try
            {
                using var resp = await http.GetAsync($"{baseUrl}/health").ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return true;
            }
            catch { }

            if (attempt % 6 == 0 && string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
                await StartServicesAsync("GrunflexPOSAPI", "PosEdgePostgres").ConfigureAwait(false);

            if (attempt == 9 && string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
                await Task.Run(() => IC.TryStartGrunflexApiProcess(_payloadRoot)).ConfigureAwait(false);

            await Task.Delay(2000).ConfigureAwait(false);
        }

        return false;
    }
}
