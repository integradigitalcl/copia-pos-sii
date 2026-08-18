using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace GrunflexPOS2.Services.Discovery;

/// <summary>
/// Cliente de auto-discovery LAN (Fase 2.5).
///
/// Envía un broadcast UDP <c>GFPOS-DISCOVER\n{requestId}</c> al puerto 33279 en todas las
/// subredes locales y colecta respuestas durante una ventana de tiempo. Devuelve servidores
/// únicos por IP. Funciona en redes Windows domésticas típicas (sin requerir mDNS ni SSDP).
/// </summary>
public sealed class ServerDiscoveryService
{
    public const int DiscoveryPort = 33279;
    public const string Magic = "GFPOS-DISCOVER";

    /// <summary>Realiza un escaneo y devuelve los servidores encontrados.</summary>
    /// <param name="timeout">Ventana de espera para respuestas (default 2.5s).</param>
    public async Task<IReadOnlyList<DiscoveredServer>> ScanAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var ttl = timeout ?? TimeSpan.FromSeconds(2.5);
        var requestId = Guid.NewGuid().ToString("N")[..8];
        var payload = Encoding.UTF8.GetBytes($"{Magic}\n{requestId}");

        var found = new Dictionary<string, DiscoveredServer>(StringComparer.OrdinalIgnoreCase);
        var sockets = new List<UdpClient>();

        try
        {
            // Una socket por interfaz local con broadcast habilitado.
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
                catch { /* ignorar interfaces que no permitan bind */ }
            }

            // Fallback: al menos una socket en 0.0.0.0 si no hay interfaces específicas
            if (sockets.Count == 0)
            {
                var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                sockets.Add(udp);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ttl);

            // Tareas de recepción por socket
            var rxTasks = sockets.Select(s => Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var result = await s.ReceiveAsync(cts.Token).ConfigureAwait(false);
                        var text = Encoding.UTF8.GetString(result.Buffer);
                        if (string.IsNullOrEmpty(text) || text.StartsWith(Magic, StringComparison.Ordinal))
                            continue; // ignorar ecos de nuestro propio broadcast
                        var srv = TryParse(text);
                        if (srv == null) continue;
                        if (!string.IsNullOrEmpty(requestId) && !string.IsNullOrEmpty(srv.RequestId) &&
                            !string.Equals(requestId, srv.RequestId, StringComparison.Ordinal))
                            continue; // ignorar respuestas a otro escaneo
                        lock (found)
                        {
                            if (!found.ContainsKey(srv.Ip))
                                found[srv.Ip] = srv;
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { /* seguir escuchando */ }
                }
            }, cts.Token)).ToList();

            // Enviar broadcasts varios ciclos por si hay paquetes perdidos
            var broadcastEp = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
            for (var i = 0; i < 3 && !cts.IsCancellationRequested; i++)
            {
                foreach (var s in sockets)
                {
                    try { await s.SendAsync(payload, payload.Length, broadcastEp).ConfigureAwait(false); }
                    catch { }
                }
                try { await Task.Delay(400, cts.Token).ConfigureAwait(false); }
                catch { break; }
            }

            try { await Task.WhenAll(rxTasks).ConfigureAwait(false); }
            catch { /* cancelled */ }
        }
        finally
        {
            foreach (var s in sockets) { try { s.Dispose(); } catch { } }
        }

        return found.Values
            .OrderBy(s => s.Server, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Ip, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DiscoveredServer? TryParse(string json)
    {
        try
        {
            var data = JsonSerializer.Deserialize<DiscoveryResponseDto>(json, JsonOpts);
            if (data == null || string.IsNullOrWhiteSpace(data.Ip)) return null;
            return new DiscoveredServer
            {
                Server = data.Server ?? string.Empty,
                Ip = data.Ip,
                ApiPort = data.ApiPort <= 0 ? 7279 : data.ApiPort,
                ApiBaseUrl = string.IsNullOrWhiteSpace(data.ApiBaseUrl) ? $"http://{data.Ip}:7279/" : data.ApiBaseUrl,
                Version = data.Version ?? string.Empty,
                RequestId = data.RequestId ?? string.Empty
            };
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<IPAddress> EnumerateLocalIPv4Addresses()
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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class DiscoveryResponseDto
    {
        [JsonPropertyName("server")]     public string? Server { get; set; }
        [JsonPropertyName("ip")]         public string? Ip { get; set; }
        [JsonPropertyName("apiPort")]    public int     ApiPort { get; set; }
        [JsonPropertyName("apiBaseUrl")] public string? ApiBaseUrl { get; set; }
        [JsonPropertyName("version")]    public string? Version { get; set; }
        [JsonPropertyName("requestId")]  public string? RequestId { get; set; }
    }
}
