using System.Net.Http;
using System.Net.Http.Json;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Connectivity;

namespace GrunflexPOS2.Services.Multicaja.Terminal;

/// <summary>Contexto de terminal con cache TTL y validación activa.</summary>
public sealed class TerminalContextService
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan OfflineThreshold = TimeSpan.FromMinutes(3);

    private readonly TerminalService _terminal;
    private readonly HttpClient _http;

    public TerminalContextService(TerminalService terminal, HttpClient http)
    {
        _terminal = terminal;
        _http = http;
    }

    public async Task<TerminalContextCache> RequireCurrentTerminalAsync(CancellationToken ct = default)
    {
        var ctx = await GetCurrentTerminalAsync(ct).ConfigureAwait(false);
        if (ctx == null || !ctx.Active)
            throw new InvalidOperationException("Terminal no registrada o inactiva.");
        return ctx;
    }

    public async Task<TerminalContextCache?> GetCurrentTerminalAsync(CancellationToken ct = default)
    {
        var cached = _terminal.GetCachedContext();
        if (cached != null)
            return cached;

        if (_terminal.TerminalId is not { } tid || tid == Guid.Empty)
            return null;

        try
        {
            var resp = await ResilientHttp.SendWithRetryAsync(c =>
                _http.PostAsJsonAsync("api/terminals/enterprise/validate", new
                {
                    TerminalId = tid,
                    InstallationId = _terminal.InstallationId,
                    TerminalToken = _terminal.TerminalToken
                }, c), ct).ConfigureAwait(false);

            if (resp == null || !resp.IsSuccessStatusCode)
                return BuildLocalFallback();

            var body = await resp.Content.ReadFromJsonAsync<ValidateReply>(cancellationToken: ct).ConfigureAwait(false);
            if (body == null || !body.Valid)
                return null;

            var ctx = new TerminalContextCache
            {
                TerminalId = tid,
                InstallationId = _terminal.InstallationId,
                CajaId = body.CajaId ?? _terminal.Identity.CajaId,
                Active = body.Active,
                CachedAtUtc = DateTime.UtcNow
            };
            _terminal.SetCachedContext(ctx, CacheTtl);
            return ctx;
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("TerminalContextService.GetCurrentTerminalAsync", ex);
            return BuildLocalFallback();
        }
    }

    public void InvalidateCache() => _terminal.InvalidateCache();

    public static bool IsOffline(DateTime? lastSeenUtc) =>
        lastSeenUtc == null || DateTime.UtcNow - lastSeenUtc.Value > OfflineThreshold;

    private TerminalContextCache? BuildLocalFallback()
    {
        if (_terminal.TerminalId is not { } tid || tid == Guid.Empty)
            return null;
        var ctx = new TerminalContextCache
        {
            TerminalId = tid,
            InstallationId = _terminal.InstallationId,
            CajaId = _terminal.Identity.CajaId,
            Active = true
        };
        _terminal.SetCachedContext(ctx, TimeSpan.FromSeconds(30));
        return ctx;
    }

    private sealed class ValidateReply
    {
        public bool Valid { get; set; }
        public bool Active { get; set; }
        public Guid? CajaId { get; set; }
        public DateTime? LastSeenAtUtc { get; set; }
    }
}
