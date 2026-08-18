using System.Net.Http;
using System.Net.Http.Json;

namespace GrunflexPOS2.Services.Multicaja;

public sealed class MulticajaCapabilities
{
    public string ApiVersion { get; set; } = "";
    public bool IncrementalSync { get; set; }
    public bool SignalR { get; set; }
    public bool Idempotency { get; set; }
    public bool OfflineReplay { get; set; }
    public int HeartbeatVersion { get; set; } = 1;
    public bool TerminalIdentity { get; set; }
    public bool FullCatalogPull { get; set; } = true;
}

public static class MulticajaCapabilitiesClient
{
    private static MulticajaCapabilities? _cached;
    private static DateTime _cachedAt = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public static async Task<MulticajaCapabilities> GetAsync(HttpClient http, CancellationToken ct = default)
    {
        if (_cached != null && DateTime.UtcNow - _cachedAt < CacheTtl)
            return _cached;

        try
        {
            var resp = await http.GetAsync("api/multicaja/capabilities", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return DefaultCapabilities();

            var json = await resp.Content.ReadFromJsonAsync<CapabilitiesDto>(cancellationToken: ct).ConfigureAwait(false);
            _cached = new MulticajaCapabilities
            {
                ApiVersion = json?.ApiVersion ?? "",
                IncrementalSync = json?.IncrementalSync ?? false,
                SignalR = json?.SignalR ?? false,
                Idempotency = json?.Idempotency ?? false,
                OfflineReplay = json?.OfflineReplay ?? false,
                HeartbeatVersion = json?.HeartbeatVersion ?? 1,
                TerminalIdentity = json?.TerminalIdentity ?? false,
                FullCatalogPull = json?.FullCatalogPull ?? true
            };
            _cachedAt = DateTime.UtcNow;
            PosDiagnostics.Log(
                $"multicaja.capabilities incremental={_cached.IncrementalSync} signalR={_cached.SignalR} heartbeatV={_cached.HeartbeatVersion}");
            return _cached;
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("MulticajaCapabilitiesClient", ex);
            return DefaultCapabilities();
        }
    }

    public static void Invalidate() => _cached = null;

    private static MulticajaCapabilities DefaultCapabilities() => new()
    {
        FullCatalogPull = true,
        HeartbeatVersion = 1
    };

    private sealed class CapabilitiesDto
    {
        public string? ApiVersion { get; set; }
        public bool IncrementalSync { get; set; }
        public bool SignalR { get; set; }
        public bool Idempotency { get; set; }
        public bool OfflineReplay { get; set; }
        public int HeartbeatVersion { get; set; }
        public bool TerminalIdentity { get; set; }
        public bool FullCatalogPull { get; set; }
    }
}
