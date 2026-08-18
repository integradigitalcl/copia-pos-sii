using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using GrunflexPOS2.Data;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Connectivity;
using GrunflexPOS2.Services.Licensing;

namespace GrunflexPOS2.Services.Multicaja.Terminal;

/// <summary>Registro y heartbeat enterprise contra la API.</summary>
public sealed class TerminalService
{
    private readonly HttpClient _http;
    private readonly TerminalIdentityStore _store = new();
    private Timer? _heartbeatTimer;
    private TerminalIdentitySnapshot _identity;
    private DateTime _cacheExpiresUtc = DateTime.MinValue;
    private TerminalContextCache? _cachedContext;

    public TerminalService(HttpClient http)
    {
        _http = http;
        _identity = _store.LoadOrCreate();
    }

    public TerminalIdentitySnapshot Identity => _identity;

    public Guid InstallationId => _identity.InstallationId;
    public Guid? TerminalId => _identity.TerminalId;
    public string TerminalToken => _identity.TerminalToken;

    public bool LastGranted { get; private set; }
    public string? LastReason { get; private set; }

    public async Task EnsureRegisteredAsync(CancellationToken ct = default)
    {
        try
        {
            var cfg = AppConfig.Cargar();
            if (Guid.TryParse(cfg.CajaId, out var caja) && caja != Guid.Empty)
                _identity.CajaId = caja;

            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
            var activationId = TryGetActivationId() ?? "";

            var resp = await ResilientHttp.SendWithRetryAsync(c =>
                _http.PostAsJsonAsync("api/terminals/enterprise/register", new
                {
                    InstallationId = _identity.InstallationId,
                    TerminalToken = _identity.TerminalToken,
                    CajaId = _identity.CajaId,
                    BranchId = _identity.BranchId,
                    DisplayName = _identity.DisplayName,
                    MachineFingerprint = TerminalRegistrationClient.MachineFingerprint(),
                    MachineName = Environment.MachineName,
                    Version = version,
                    ActivationId = activationId
                }, c), ct).ConfigureAwait(false);

            if (resp == null || !resp.IsSuccessStatusCode)
            {
                LastReason = $"Registro enterprise HTTP {resp?.StatusCode}";
                PosDiagnostics.Log("multicaja.terminal.register: " + LastReason);
                return;
            }

            var body = await resp.Content.ReadFromJsonAsync<RegisterReply>(cancellationToken: ct).ConfigureAwait(false);
            if (body == null) return;
            LastGranted = body.Granted;
            LastReason = body.Reason;
            if (body.Granted && body.TerminalId is { } tid && tid != Guid.Empty)
            {
                _identity.TerminalId = tid;
                _identity.RegisteredAtUtc = DateTime.UtcNow;
                _store.Save(_identity);
                InvalidateCache();
            }

            PosDiagnostics.Log(
                $"multicaja.terminal.register granted={body.Granted} id={body.TerminalId} slots={body.SlotsInUse}/{body.SlotsTotal}");
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("TerminalService.EnsureRegisteredAsync", ex);
        }
    }

    public void StartHeartbeat(TimeSpan? interval = null)
    {
        StopHeartbeat();
        var period = interval ?? TimeSpan.FromSeconds(75);
        _heartbeatTimer = new Timer(_ => _ = SendHeartbeatAsync(), null, TimeSpan.Zero, period);
    }

    public void StopHeartbeat() => _heartbeatTimer?.Dispose();

    public Task SendHeartbeatNowAsync(bool reconnect = false, CancellationToken ct = default) =>
        SendHeartbeatAsync(reconnect, ct);

    private async Task SendHeartbeatAsync(bool reconnect = false, CancellationToken ct = default)
    {
        if (_identity.TerminalId is not { } tid || tid == Guid.Empty)
            return;

        try
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
            Guid? userId = App.UsuarioActual?.Id;
            Guid? sessionId = App.MulticajaSesionEnServidor?.Id;

            var resp = await ResilientHttp.SendWithRetryAsync(c =>
                _http.PostAsJsonAsync("api/terminals/enterprise/heartbeat", new
                {
                    TerminalId = tid,
                    InstallationId = _identity.InstallationId,
                    TerminalToken = _identity.TerminalToken,
                    CajaId = _identity.CajaId ?? App.CajaActualId,
                    CurrentUserId = userId,
                    CurrentSessionId = sessionId,
                    Version = version,
                    DisplayName = _identity.DisplayName,
                    Reconnect = reconnect
                }, c), ct).ConfigureAwait(false);

            if (resp == null || !resp.IsSuccessStatusCode)
            {
                PosDiagnostics.Log($"multicaja.heartbeat HTTP {resp?.StatusCode}");
                return;
            }

            var body = await resp.Content.ReadFromJsonAsync<HeartbeatReply>(cancellationToken: ct).ConfigureAwait(false);
            if (body != null && !body.Authorized)
                PosDiagnostics.Log("multicaja.heartbeat unauthorized: " + (body.Reason ?? "-"));
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("multicaja.heartbeat error", ex);
        }
    }

    public TerminalContextCache? GetCachedContext() =>
        _cachedContext != null && DateTime.UtcNow < _cacheExpiresUtc ? _cachedContext : null;

    public void SetCachedContext(TerminalContextCache ctx, TimeSpan ttl)
    {
        _cachedContext = ctx;
        _cacheExpiresUtc = DateTime.UtcNow + ttl;
    }

    public void InvalidateCache()
    {
        _cachedContext = null;
        _cacheExpiresUtc = DateTime.MinValue;
    }

    private static string? TryGetActivationId()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GrunflexPOS", "license", "activation.json");
            if (!File.Exists(path)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("activationId", out var v))
                return v.GetString();
        }
        catch { }
        return null;
    }

    private sealed class RegisterReply
    {
        public bool Granted { get; set; }
        public string? Reason { get; set; }
        public int SlotsInUse { get; set; }
        public int SlotsTotal { get; set; }
        public Guid? TerminalId { get; set; }
    }

    private sealed class HeartbeatReply
    {
        public bool Authorized { get; set; }
        public string? Reason { get; set; }
    }
}

public sealed class TerminalContextCache
{
    public Guid TerminalId { get; init; }
    public Guid InstallationId { get; init; }
    public Guid? CajaId { get; init; }
    public bool Active { get; init; } = true;
    public DateTime CachedAtUtc { get; init; } = DateTime.UtcNow;
}
