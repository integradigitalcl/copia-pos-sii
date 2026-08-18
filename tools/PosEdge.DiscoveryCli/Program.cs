using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Makaretu.Dns;

// Prints ONLY the discovered apiBaseUrl on success, and exits 0.
// On failure, prints nothing (or minimal error to stderr) and exits non-zero.
//
// Usage:
//   PosEdge.DiscoveryCli.exe [--prefer-udp] [--port 5071] [--timeout-ms 2500]
//
// Protocol (UDP):
// - Client broadcasts: "POSEDGE-DISCOVER\n{requestId}" to UDP 33279
// - Server responds with JSON: { apiBaseUrl: "http://ip:5071/", requestId: "..." , ... }

var argv = args;
var preferUdp = argv.Contains("--prefer-udp", StringComparer.OrdinalIgnoreCase);
var port = ReadIntArg(argv, "--port", 5071);
var timeoutMs = ReadIntArg(argv, "--timeout-ms", 2500);

var apiBase = preferUdp
    ? await TryUdpDiscoveryAsync(port, timeoutMs)
    : await TrySubnetScanAsync(port, timeoutMs);

apiBase ??= await TryMdnsDiscoveryAsync(timeoutMs);
apiBase ??= await TrySubnetScanAsync(port, timeoutMs);

if (!string.IsNullOrWhiteSpace(apiBase))
{
    Console.Out.Write(apiBase);
    Environment.Exit(0);
}

Environment.Exit(2);

static int ReadIntArg(string[] argv, string key, int fallback)
{
    for (var i = 0; i < argv.Length - 1; i++)
    {
        if (!string.Equals(argv[i], key, StringComparison.OrdinalIgnoreCase))
            continue;
        if (int.TryParse(argv[i + 1], out var v) && v > 0)
            return v;
    }
    return fallback;
}

static async Task<string?> TryUdpDiscoveryAsync(int apiPort, int timeoutMs)
{
    const int discoveryPort = 33279;
    const string magic = "POSEDGE-DISCOVER";

    var requestId = Guid.NewGuid().ToString("N")[..8];
    var payload = Encoding.UTF8.GetBytes($"{magic}\n{requestId}");

    var found = new Dictionary<string, (string apiBaseUrl, int apiPort)>(StringComparer.OrdinalIgnoreCase);
    var sockets = new List<UdpClient>();

    try
    {
        foreach (var ip in EnumerateLocalIPv4Addresses())
        {
            try
            {
                var udp = new UdpClient(AddressFamily.InterNetwork)
                {
                    EnableBroadcast = true,
                    ExclusiveAddressUse = false
                };
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(ip, 0));
                sockets.Add(udp);
            }
            catch { }
        }

        if (sockets.Count == 0)
        {
            var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            sockets.Add(udp);
        }

        using var cts = new CancellationTokenSource(timeoutMs);

        var rxTasks = sockets.Select(s => Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var result = await s.ReceiveAsync(cts.Token).ConfigureAwait(false);
                    var text = Encoding.UTF8.GetString(result.Buffer);
                    var srv = TryParseDiscoveryJson(text);
                    if (srv == null) continue;
                    if (!string.Equals(srv.RequestId ?? "", requestId, StringComparison.Ordinal)) continue;
                    if (string.IsNullOrWhiteSpace(srv.ApiBaseUrl)) continue;

                    lock (found)
                    {
                        found[srv.ApiBaseUrl] = (srv.ApiBaseUrl, srv.ApiPort);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }, cts.Token)).ToList();

        var broadcastEp = new IPEndPoint(IPAddress.Broadcast, discoveryPort);
        for (var i = 0; i < 3 && !cts.IsCancellationRequested; i++)
        {
            foreach (var s in sockets)
            {
                try { await s.SendAsync(payload, payload.Length, broadcastEp).ConfigureAwait(false); }
                catch { }
            }
            try { await Task.Delay(350, cts.Token).ConfigureAwait(false); } catch { break; }
        }

        try { await Task.WhenAll(rxTasks).ConfigureAwait(false); } catch { }
    }
    finally
    {
        foreach (var s in sockets) { try { s.Dispose(); } catch { } }
    }

    // Pick the first server that matches expected API port if provided, otherwise any.
    lock (found)
    {
        var match = found.Values.FirstOrDefault(v => v.apiPort == apiPort);
        if (!string.IsNullOrWhiteSpace(match.apiBaseUrl))
            return EnsureTrailingSlash(match.apiBaseUrl);
        var any = found.Values.FirstOrDefault();
        return !string.IsNullOrWhiteSpace(any.apiBaseUrl) ? EnsureTrailingSlash(any.apiBaseUrl) : null;
    }
}

static async Task<string?> TrySubnetScanAsync(int port, int timeoutMs)
{
    // Fallback scan: find local /24 networks and try GET /health/live quickly.
    var prefixes = EnumerateLocalIPv4Networks24().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    if (prefixes.Count == 0) return null;

    using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(250, Math.Min(timeoutMs, 2000))) };
    var cts = new CancellationTokenSource(timeoutMs);

    foreach (var prefix in prefixes)
    {
        // Try a small subset first (common router ranges): .1, .10, .100, .101, .102, .200
        var firstTry = new[] { 1, 10, 100, 101, 102, 200 };
        foreach (var host in firstTry)
        {
            var ip = $"{prefix}.{host}";
            var url = $"http://{ip}:{port}/health/live";
            if (await IsLiveAsync(http, url, cts.Token).ConfigureAwait(false))
                return $"http://{ip}:{port}/";
        }

        // Then scan rest but with a hard cap (pilot LAN, keep it fast).
        for (var host = 2; host <= 254 && !cts.IsCancellationRequested; host++)
        {
            var ip = $"{prefix}.{host}";
            var url = $"http://{ip}:{port}/health/live";
            if (await IsLiveAsync(http, url, cts.Token).ConfigureAwait(false))
                return $"http://{ip}:{port}/";
        }
    }

    return null;
}

static async Task<string?> TryMdnsDiscoveryAsync(int timeoutMs)
{
    // Minimal mDNS: query a fixed service name. Server advertises this.
    // We do not depend on Bonjour; this uses pure .NET multicast DNS.
    const string service = "_posedgetcp._tcp.local";

    try
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var resolver = new ServiceDiscovery();
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        resolver.ServiceInstanceDiscovered += (s, e) =>
        {
            try
            {
                var name = e.ServiceInstanceName?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(name) &&
                    name.EndsWith(service, StringComparison.OrdinalIgnoreCase))
                {
                    // We can't reliably extract host/port without SRV/TXT resolution in this minimal version,
                    // so just signal "mdns found" and let the caller fall back to subnet scan quickly.
                    tcs.TrySetResult("mdns://found");
                }
            }
            catch { }
        };

        // Kick discovery
        resolver.QueryServiceInstances(service);
        resolver.Mdns.Start();

        await using (cts.Token.Register(() => tcs.TrySetResult(null)))
        {
            var r = await tcs.Task.ConfigureAwait(false);
            return string.Equals(r, "mdns://found", StringComparison.Ordinal) ? null : r;
        }
    }
    catch
    {
        return null;
    }
}

static async Task<bool> IsLiveAsync(HttpClient http, string url, CancellationToken ct)
{
    try
    {
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;
    }
    catch { return false; }
}

static IEnumerable<IPAddress> EnumerateLocalIPv4Addresses()
{
    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (ni.OperationalStatus != OperationalStatus.Up) continue;
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
        var props = ni.GetIPProperties();
        foreach (var ua in props.UnicastAddresses)
        {
            if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                yield return ua.Address;
        }
    }
}

static IEnumerable<string> EnumerateLocalIPv4Networks24()
{
    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (ni.OperationalStatus != OperationalStatus.Up) continue;
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
        var props = ni.GetIPProperties();
        foreach (var ua in props.UnicastAddresses)
        {
            if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
            var b = ua.Address.GetAddressBytes();
            if (b.Length != 4) continue;
            yield return $"{b[0]}.{b[1]}.{b[2]}";
        }
    }
}

static DiscoveryResponseDto? TryParseDiscoveryJson(string json)
{
    try
    {
        var dto = JsonSerializer.Deserialize<DiscoveryResponseDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return dto;
    }
    catch
    {
        return null;
    }
}

static string EnsureTrailingSlash(string s) => s.EndsWith("/") ? s : s + "/";

file sealed class DiscoveryResponseDto
{
    public string? ApiBaseUrl { get; set; }
    public int ApiPort { get; set; }
    public string? RequestId { get; set; }
    public string? ClusterId { get; set; }
    public string? Fingerprint { get; set; }
}
