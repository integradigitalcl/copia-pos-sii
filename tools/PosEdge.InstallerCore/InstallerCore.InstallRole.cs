using System.Text;

namespace PosEdge.InstallerCore;

public static partial class InstallerCore
{
    public const string InstallForcedRoleFileName = "install-forced-role.txt";
    public const string InstallServerHostFileName = "install-server-host.txt";

    public const int GrunflexApiPort = 7279;

    public static string? ReadInstallForcedRole()
    {
        var path = Path.Combine(ConfigDir, InstallForcedRoleFileName);
        var v = SafeReadText(path);
        if (string.IsNullOrWhiteSpace(v))
            return null;
        v = v.Trim().ToLowerInvariant();
        return v is "server" or "terminal" ? v : null;
    }

    public static string? ReadInstallServerHost()
    {
        var path = Path.Combine(ConfigDir, InstallServerHostFileName);
        var v = SafeReadText(path);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    /// <summary>IP/nombre del servidor escrito por el instalador (se conserva tras la instalación).</summary>
    public const string PersistedServerHostFileName = "configured-server-host.txt";

    public static void PersistInstallServerHost(string hostOrIp)
    {
        if (string.IsNullOrWhiteSpace(hostOrIp))
            return;
        try
        {
            EnsureConfigDir();
            var v = hostOrIp.Trim();
            File.WriteAllText(Path.Combine(ConfigDir, PersistedServerHostFileName), v, Encoding.UTF8);
            File.WriteAllText(Path.Combine(ConfigDir, InstallServerHostFileName), v, Encoding.UTF8);
        }
        catch { /* noop */ }
    }

    public static string? ReadPersistedServerHost()
    {
        var v = SafeReadText(Path.Combine(ConfigDir, PersistedServerHostFileName));
        if (!string.IsNullOrWhiteSpace(v))
            return v.Trim();
        return ReadInstallServerHost();
    }

    public static void ClearInstallRoleHintFiles()
    {
        try
        {
            var p = Path.Combine(ConfigDir, InstallForcedRoleFileName);
            if (File.Exists(p))
                File.Delete(p);
        }
        catch { /* noop */ }
    }

    public static string BuildGrunflexApiBaseUrl(string hostOrIp)
    {
        var h = hostOrIp.Trim().TrimEnd('/');
        if (h.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            h.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return h.EndsWith('/') ? h : h + "/";
        return $"http://{h}:{GrunflexApiPort}/";
    }

    public static async Task<bool> IsGrunflexApiHealthyAsync(string apiBase, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var baseUrl = apiBase.TrimEnd('/');
            using var live = await http.GetAsync(baseUrl + "/health/live", ct);
            if (live.IsSuccessStatusCode)
                return true;
            using var ready = await http.GetAsync(baseUrl + "/health/ready", ct);
            return ready.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string?> ResolveGrunflexServerApiAsync(string discoveryExePath, CancellationToken ct = default)
    {
        var manualHost = ReadInstallServerHost() ?? ReadPersistedServerHost();
        if (!string.IsNullOrWhiteSpace(manualHost))
        {
            var manualUrl = BuildGrunflexApiBaseUrl(manualHost);
            if (await IsGrunflexApiHealthyAsync(manualUrl, ct))
                return manualUrl;

            // IP ingresada en el instalador: usar aunque /health falle (firewall, API aún iniciando).
            return manualUrl;
        }

        if (File.Exists(discoveryExePath))
        {
            var discovered = await RunDiscoveryAsync(discoveryExePath, GrunflexApiPort, ct);
            if (discovered != null && await IsGrunflexApiHealthyAsync(discovered, ct))
                return discovered;
        }

        var cachedPath = Path.Combine(ConfigDir, "cached-server.txt");
        var cached = SafeReadText(cachedPath);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            var url = cached.Trim();
            if (!url.EndsWith('/'))
                url += "/";
            if (await IsGrunflexApiHealthyAsync(url, ct))
                return url;
        }

        return null;
    }

    private static string? SafeReadText(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return File.ReadAllText(path, Encoding.UTF8).Trim();
        }
        catch
        {
            return null;
        }
    }
}
