using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GrunflexPOS2.Data;
using GrunflexPOS2.Licensing;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Connectivity;

namespace GrunflexPOS2.Services.Licensing;

/// <summary>
/// Cliente del endpoint de licenciamiento server-side (Fase 5.1).
///
/// Modos:
/// - <b>Soft (default)</b>: si <c>AppConfig.LicenseServerEnforcement = false</c>, sólo registra y
///   loguea. Una respuesta de denegación no bloquea el POS. Útil para rodaje.
/// - <b>Hard</b>: si <c>true</c>, una denegación detiene el arranque del POS (a través del
///   código que invoca esta clase: el caller decide cómo cortar la sesión).
///
/// El POS llama <see cref="EnsureRegisteredAsync"/> al arrancar y luego heartbeat cada 5 min
/// mientras esté online.
/// </summary>
public sealed class TerminalRegistrationClient
{
    private readonly HttpClient _http;
    private Timer? _heartbeatTimer;

    public bool LastGranted { get; private set; }
    public string? LastReason { get; private set; }
    public int SlotsInUse { get; private set; }
    public int SlotsTotal { get; private set; }

    public TerminalRegistrationClient(HttpClient http)
    {
        _http = http;
    }

    public static string MachineFingerprint()
    {
        try
        {
            var primary = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Select(n => n.GetPhysicalAddress()?.ToString())
                .FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "";
            var seed = $"{primary}|{Environment.MachineName}|{Environment.GetEnvironmentVariable("USERDOMAIN")}";
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
            return Convert.ToHexString(bytes).ToLowerInvariant()[..32];
        }
        catch
        {
            return "fp-" + Environment.MachineName.ToLowerInvariant();
        }
    }

    public async Task<bool> EnsureRegisteredAsync(CancellationToken ct = default)
    {
        try
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
            var activationId = TryGetActivationId() ?? string.Empty;

            var resp = await ResilientHttp.SendWithRetryAsync(c =>
                _http.PostAsJsonAsync("api/terminals/register", new
                {
                    MachineFingerprint = MachineFingerprint(),
                    MachineName = Environment.MachineName,
                    Version = version,
                    ActivationId = activationId
                }, c),
                ct).ConfigureAwait(false);

            if (resp == null || !resp.IsSuccessStatusCode)
            {
                LastGranted = false;
                LastReason = $"No fue posible registrar (HTTP {resp?.StatusCode}).";
                PosDiagnostics.Log("Terminal register: " + LastReason);
                return !AppConfig.Cargar().LicenseServerEnforcement;
            }
            var body = await resp.Content.ReadFromJsonAsync<RegisterReply>(cancellationToken: ct).ConfigureAwait(false);
            if (body == null)
            {
                LastGranted = false;
                LastReason = "Respuesta vacía del servidor.";
                return !AppConfig.Cargar().LicenseServerEnforcement;
            }

            LastGranted = body.Granted;
            LastReason = body.Reason;
            SlotsInUse = body.SlotsInUse;
            SlotsTotal = body.SlotsTotal;
            PosDiagnostics.Log($"Terminal register: granted={body.Granted} slots={body.SlotsInUse}/{body.SlotsTotal} reason={body.Reason ?? "-"}");

            if (!body.Granted && AppConfig.Cargar().LicenseServerEnforcement)
                return false;

            return true;
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("EnsureRegisteredAsync error", ex);
            return !AppConfig.Cargar().LicenseServerEnforcement;
        }
    }

    public void StartHeartbeat(TimeSpan? interval = null)
    {
        StopHeartbeat();
        var period = interval ?? TimeSpan.FromSeconds(90);
        _heartbeatTimer = new Timer(_ => _ = SendHeartbeatAsync(),
            null,
            period,
            period);
    }

    public void StopHeartbeat()
    {
        try { _heartbeatTimer?.Dispose(); } catch { }
        _heartbeatTimer = null;
    }

    public Task SendHeartbeatNowAsync(CancellationToken ct = default) =>
        SendHeartbeatAsync(ct);

    private async Task SendHeartbeatAsync(CancellationToken ct = default)
    {
        try
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
            var resp = await ResilientHttp.SendWithRetryAsync(c =>
                _http.PostAsJsonAsync("api/terminals/heartbeat", new
                {
                    MachineFingerprint = MachineFingerprint(),
                    Version = version,
                    MachineName = Environment.MachineName
                }, c), ct).ConfigureAwait(false);
            if (resp == null) return;
            if (!resp.IsSuccessStatusCode)
            {
                PosDiagnostics.Log("Heartbeat HTTP " + resp.StatusCode);
                return;
            }
            var body = await resp.Content.ReadFromJsonAsync<HeartbeatReply>(cancellationToken: ct).ConfigureAwait(false);
            if (body != null && !body.Authorized)
            {
                PosDiagnostics.Log("Heartbeat: NO autorizado. Razón: " + (body.Reason ?? "-"));
            }
        }
        catch (Exception ex) { PosDiagnostics.Log("Heartbeat error", ex); }
    }

    private static string? TryGetActivationId()
    {
        try
        {
            var cfg = new ConfiguracionService();
            var fromConfig = cfg.Get("licencia_activation_id")?.Trim();
            if (!string.IsNullOrWhiteSpace(fromConfig))
                return fromConfig;

            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GrunflexPOS", "license", "activation.json");
            if (!System.IO.File.Exists(path)) return null;
            var json = System.IO.File.ReadAllText(path);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
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
    }

    private sealed class HeartbeatReply
    {
        public bool Authorized { get; set; }
        public string? Reason { get; set; }
    }
}
