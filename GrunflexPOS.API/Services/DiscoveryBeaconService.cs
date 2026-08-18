using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GrunflexPOS.API.Services;

/// <summary>
/// Servicio de descubrimiento LAN (Fase 2.5).
///
/// Escucha broadcasts UDP en el puerto <c>33279</c>. Cuando un cliente envía
/// "GFPOS-DISCOVER\n[requestId]", responde unicast con un JSON describiendo
/// el servidor (hostname, IPv4, puerto de la API, versión).
///
/// El protocolo es texto plano + JSON, sin auth (LAN privada). El cliente luego
/// usa la URL HTTP para handshake real con tokens.
/// </summary>
public sealed class DiscoveryBeaconService : BackgroundService
{
    public const int DiscoveryPort = 33279;
    public const string Magic = "GFPOS-DISCOVER";

    private readonly ILogger<DiscoveryBeaconService> _log;
    private readonly IConfiguration _cfg;

    public DiscoveryBeaconService(ILogger<DiscoveryBeaconService> log, IConfiguration cfg)
    {
        _log = log;
        _cfg = cfg;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _log.LogInformation("Discovery beacon escuchando en UDP {Port}", DiscoveryPort);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "No se pudo iniciar discovery beacon en UDP {Port}. El auto-discovery quedará deshabilitado.", DiscoveryPort);
            udp?.Dispose();
            return;
        }

        var apiPort = ResolveApiPort();
        var version = typeof(DiscoveryBeaconService).Assembly.GetName().Version?.ToString() ?? "1.0.0";

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(stoppingToken).ConfigureAwait(false);
                var payload = Encoding.UTF8.GetString(result.Buffer);
                if (string.IsNullOrEmpty(payload) || !payload.StartsWith(Magic, StringComparison.Ordinal))
                    continue;

                var requestId = payload.Length > Magic.Length + 1
                    ? payload[(Magic.Length + 1)..].Trim()
                    : string.Empty;

                var ip = BestLocalIp(result.RemoteEndPoint.Address);
                var response = new
                {
                    server = Environment.MachineName,
                    ip,
                    apiPort,
                    apiBaseUrl = $"http://{ip}:{apiPort}/",
                    version,
                    requestId
                };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
                await udp.SendAsync(bytes, bytes.Length, result.RemoteEndPoint).ConfigureAwait(false);
                _log.LogDebug("Discovery responded to {Endpoint} (reqId={ReqId})", result.RemoteEndPoint, requestId);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Error procesando paquete de discovery; continuando.");
                await Task.Delay(250, stoppingToken).ConfigureAwait(false);
            }
        }

        udp.Dispose();
        _log.LogInformation("Discovery beacon detenido.");
    }

    /// <summary>Lee el puerto donde la API publica HTTP (urls / Kestrel). Default 7279.</summary>
    private int ResolveApiPort()
    {
        try
        {
            var urls = _cfg["urls"] ?? _cfg["ASPNETCORE_URLS"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
            if (!string.IsNullOrWhiteSpace(urls))
            {
                var first = urls.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(first) && Uri.TryCreate(first, UriKind.Absolute, out var uri))
                    return uri.Port;
            }
        }
        catch { }
        return 7279;
    }

    /// <summary>Elige la IPv4 local que comparte subred con quien preguntó (si es posible).</summary>
    private static string BestLocalIp(IPAddress remote)
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(u => new { Addr = u.Address, Mask = u.IPv4Mask })
                .ToList();

            if (remote.AddressFamily == AddressFamily.InterNetwork)
            {
                var rem = remote.GetAddressBytes();
                foreach (var c in candidates)
                {
                    if (c.Mask == null) continue;
                    var ip = c.Addr.GetAddressBytes();
                    var mask = c.Mask.GetAddressBytes();
                    if (mask.Length != 4 || ip.Length != 4) continue;
                    var sameSubnet = true;
                    for (var i = 0; i < 4; i++)
                    {
                        if ((ip[i] & mask[i]) != (rem[i] & mask[i])) { sameSubnet = false; break; }
                    }
                    if (sameSubnet) return c.Addr.ToString();
                }
            }

            return candidates.FirstOrDefault()?.Addr.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}
