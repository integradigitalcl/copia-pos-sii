using System;

namespace GrunflexPOS2.Services.Discovery;

/// <summary>Servidor Grunflex POS descubierto en la LAN vía broadcast UDP.</summary>
public sealed class DiscoveredServer
{
    public string Server { get; init; } = string.Empty;
    public string Ip { get; init; } = string.Empty;
    public int ApiPort { get; init; } = 7279;
    public string ApiBaseUrl { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public DateTime SeenUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Texto amigable para mostrar en una lista (nombre + IP).</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Server) ? Ip : $"{Server} ({Ip})";

    public string ConnectionStringSmb => $@"Data Source=\\{Ip}\GrunflexPOS\grunflex.db;Cache=Shared";
}
