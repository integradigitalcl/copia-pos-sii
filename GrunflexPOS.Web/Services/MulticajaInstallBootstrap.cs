using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using GrunflexPOS.Web.Data;

namespace GrunflexPOS.Web.Services;

public static class MulticajaInstallBootstrap
{
    private const int ApiPort = 7279;
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GrunflexPOS", "config");
    private static readonly string InstallRolePath = Path.Combine(ConfigDir, ".install-role");
    private static readonly string InstallerHostPath = Path.Combine(ConfigDir, ".multicaja-installer-host");
    private static readonly string RuntimeConfigPath = Path.Combine(ConfigDir, "multicaja-client.json");

    public static async Task ApplyAsync(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<LocalPosStore>();
        await store.EnsureCreatedAsync(cancellationToken);

        var role = await ReadInstallRoleAsync(store, configuration, cancellationToken);
        if (role is null)
            return;

        var apiUrl = await ResolveApiUrlAsync(role, configuration, store, logger, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            if (role == "client")
                logger.LogWarning("Caja adicional: no se pudo resolver la URL de la caja principal al iniciar.");
            return;
        }

        await store.SetSettingsAsync(new Dictionary<string, string>
        {
            ["multicaja_habilitada"] = "true",
            ["terminal_role"] = role,
            ["multicaja_api_url"] = apiUrl
        }, cancellationToken);

        await WriteInstallerHostAsync(apiUrl, cancellationToken);

        if (role == "client" && !await IsApiLiveAsync(apiUrl, cancellationToken))
            logger.LogWarning("Caja adicional: la API en {ApiUrl} no respondió al arranque.", apiUrl);
        else
            logger.LogInformation("Multicaja configurada ({Role}) → {ApiUrl}", role, apiUrl);
    }

    private static async Task<string?> ReadInstallRoleAsync(
        LocalPosStore store,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (File.Exists(InstallRolePath))
        {
            var marker = (await File.ReadAllTextAsync(InstallRolePath, cancellationToken)).Trim().ToLowerInvariant();
            if (marker is "client" or "server")
                return marker;
        }

        var storedRole = (await store.GetSettingAsync("terminal_role", string.Empty, cancellationToken)).Trim()
            .ToLowerInvariant();
        if (storedRole is "client" or "server")
            return storedRole;

        if (File.Exists(InstallerHostPath))
            return "client";

        if (File.Exists(RuntimeConfigPath))
            return "client";

        if (!configuration.GetValue("Multicaja:Enabled", false))
            return null;

        var configuredUrl = configuration["Multicaja:ApiBaseUrl"] ?? string.Empty;
        if (Uri.TryCreate(configuredUrl, UriKind.Absolute, out var uri) && !IsLoopback(uri))
            return "client";

        return "server";
    }

    private static async Task<string?> ResolveApiUrlAsync(
        string role,
        IConfiguration configuration,
        LocalPosStore store,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (role == "server")
            return NormalizeApiUrl(configuration["Multicaja:ApiBaseUrl"] ?? "http://127.0.0.1:7279/");

        var fromRuntime = ReadRuntimeApiUrl();
        if (!string.IsNullOrWhiteSpace(fromRuntime) && !IsLoopback(new Uri(fromRuntime)))
        {
            if (await IsApiLiveAsync(fromRuntime, cancellationToken))
                return fromRuntime;
            if (role == "client")
                return fromRuntime;
        }

        var fromConfig = NormalizeApiUrl(configuration["Multicaja:ApiBaseUrl"]);
        if (!string.IsNullOrWhiteSpace(fromConfig) && !IsLoopback(new Uri(fromConfig)) &&
            await IsApiLiveAsync(fromConfig, cancellationToken))
            return fromConfig;

        var fromMarker = await ReadInstallerHostUrlAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(fromMarker) && await IsApiLiveAsync(fromMarker, cancellationToken))
            return fromMarker;

        var stored = NormalizeApiUrl(await store.GetSettingAsync("multicaja_api_url", string.Empty, cancellationToken));
        if (!string.IsNullOrWhiteSpace(stored) && !IsLoopback(new Uri(stored)) &&
            await IsApiLiveAsync(stored, cancellationToken))
            return stored;

        logger.LogInformation("Buscando caja principal en la red local...");
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            var discovered = await DiscoverServerAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(discovered) && await IsApiLiveAsync(discovered, cancellationToken))
                return discovered;

            if (attempt < 6)
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        return fromConfig is not null && !IsLoopback(new Uri(fromConfig)) ? fromConfig : fromMarker ?? stored;
    }

    private static string? ReadRuntimeApiUrl()
    {
        if (!File.Exists(RuntimeConfigPath))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(RuntimeConfigPath));
            if (doc.RootElement.TryGetProperty("ApiBaseUrl", out var api))
                return NormalizeApiUrl(api.GetString());
        }
        catch
        {
            // ignore malformed runtime config
        }

        return null;
    }

    private static async Task<string?> ReadInstallerHostUrlAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(InstallerHostPath))
            return null;

        var host = (await File.ReadAllTextAsync(InstallerHostPath, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(host))
            return null;

        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return NormalizeApiUrl(host);

        return NormalizeApiUrl($"http://{host}:{ApiPort}/");
    }

    private static async Task WriteInstallerHostAsync(string apiUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri))
            return;

        Directory.CreateDirectory(ConfigDir);
        await File.WriteAllTextAsync(InstallerHostPath, uri.Host, cancellationToken);
    }

    private static string? NormalizeApiUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Trim();
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            raw = $"http://{raw.TrimEnd('/')}:{ApiPort}/";

        if (!raw.EndsWith('/'))
            raw += "/";

        return raw;
    }

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback ||
        string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> IsApiLiveAsync(string apiBaseUrl, CancellationToken cancellationToken)
    {
        try
        {
            var healthUrl = new Uri(new Uri(apiBaseUrl), "health/live");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync(healthUrl, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> DiscoverServerAsync(CancellationToken cancellationToken)
    {
        var viaUdp = await DiscoverViaUdpAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(viaUdp))
            return viaUdp;

        return await DiscoverViaSubnetScanAsync(cancellationToken);
    }

    private static async Task<string?> DiscoverViaUdpAsync(CancellationToken cancellationToken)
    {
        const string magic = "GFPOS-DISCOVER";
        var requestId = Guid.NewGuid().ToString("N")[..8];
        var payload = System.Text.Encoding.UTF8.GetBytes($"{magic}\n{requestId}");
        const int discoveryPort = 33279;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var clients = new List<UdpClient>();

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    try
                    {
                        var udp = new UdpClient(AddressFamily.InterNetwork);
                        udp.EnableBroadcast = true;
                        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        udp.Client.Bind(new IPEndPoint(ua.Address, 0));
                        clients.Add(udp);
                    }
                    catch
                    {
                        // ignore bind failures on virtual adapters
                    }
                }
            }

            if (clients.Count == 0)
            {
                var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.EnableBroadcast = true;
                clients.Add(udp);
            }

            var broadcast = new IPEndPoint(IPAddress.Broadcast, discoveryPort);
            for (var i = 0; i < 3; i++)
            {
                foreach (var udp in clients)
                {
                    try { await udp.SendAsync(payload, payload.Length, broadcast); }
                    catch { /* ignore */ }
                }

                await Task.Delay(250, cancellationToken);
            }

            while (DateTime.UtcNow < deadline)
            {
                foreach (var udp in clients)
                {
                    while (udp.Available > 0)
                    {
                        UdpReceiveResult result;
                        try { result = await udp.ReceiveAsync(cancellationToken); }
                        catch { break; }

                        var text = System.Text.Encoding.UTF8.GetString(result.Buffer);
                        System.Text.Json.JsonDocument doc;
                        try { doc = System.Text.Json.JsonDocument.Parse(text); }
                        catch { continue; }
                        using (doc)
                        {
                            var root = doc.RootElement;
                            if (!root.TryGetProperty("requestId", out var rid) || rid.GetString() != requestId)
                                continue;
                            if (!root.TryGetProperty("apiBaseUrl", out var apiBaseUrl))
                                continue;
                            if (root.TryGetProperty("apiPort", out var apiPort) &&
                                apiPort.TryGetInt32(out var port) && port != ApiPort)
                                continue;

                            return NormalizeApiUrl(apiBaseUrl.GetString());
                        }
                    }
                }

                await Task.Delay(120, cancellationToken);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            foreach (var udp in clients)
                udp.Dispose();
        }

        return null;
    }

    private static async Task<string?> DiscoverViaSubnetScanAsync(CancellationToken cancellationToken)
    {
        var prefixes = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                         ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
            .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(ua =>
            {
                var bytes = ua.Address.GetAddressBytes();
                return bytes.Length == 4 ? $"{bytes[0]}.{bytes[1]}.{bytes[2]}" : null;
            })
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();

        if (prefixes.Count == 0)
            return null;

        var preferredLastOctets = new[] { 1, 10, 11, 100, 101, 102, 200 };
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        foreach (var prefix in prefixes)
        {
            foreach (var last in preferredLastOctets)
            {
                var url = $"http://{prefix}.{last}:{ApiPort}/health/live";
                if (await ProbeHealthAsync(client, url, cancellationToken))
                    return $"http://{prefix}.{last}:{ApiPort}/";
            }

            for (var last = 2; last <= 254; last++)
            {
                if (preferredLastOctets.Contains(last))
                    continue;

                var url = $"http://{prefix}.{last}:{ApiPort}/health/live";
                if (await ProbeHealthAsync(client, url, cancellationToken))
                    return $"http://{prefix}.{last}:{ApiPort}/";
            }
        }

        return null;
    }

    private static async Task<bool> ProbeHealthAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
