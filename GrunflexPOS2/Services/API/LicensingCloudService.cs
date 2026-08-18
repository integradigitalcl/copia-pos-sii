using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using GrunflexPOS2.Data;
using GrunflexPOS2.Licensing;

namespace GrunflexPOS2.Services.API;

/// <summary>Sincronización de licencia con <c>/api/licensing</c> (refresh + estado).</summary>
public static class LicensingCloudService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Obtiene token nuevo desde el servidor y lo aplica localmente.</summary>
    public static async Task<(bool Ok, string Message)> TryRefreshAsync(bool silent, CancellationToken cancellationToken = default)
    {
        var cfg = new ConfiguracionService();
        var aid = cfg.Get("licencia_activation_id")?.Trim();
        if (string.IsNullOrWhiteSpace(aid))
            return (false, silent ? string.Empty : "No hay ActivationId en la configuración local.");

        var api = AppConfig.Cargar();
        var baseUri = api.ApiBaseUrl.TrimEnd('/') + "/";

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(baseUri), Timeout = TimeSpan.FromSeconds(25) };
            var payload = new { activationId = aid, machineName = Environment.MachineName };
            using var resp = await client.PostAsJsonAsync("api/licensing/refresh", payload, cancellationToken).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                if (text.Contains("vencida", StringComparison.OrdinalIgnoreCase))
                    ClearLicenseAndNotify(cfg);
                else
                    await ApplyStatusIfExpiredAsync(client, aid, cfg, cancellationToken).ConfigureAwait(false);

                return (false, silent ? string.Empty : $"Servidor: {(int)resp.StatusCode} {text}");
            }

            var dto = JsonSerializer.Deserialize<LicensingTokenDto>(text, JsonOpts);
            if (dto?.LicenseToken is not { Length: > 0 })
                return (false, silent ? string.Empty : "Respuesta sin token de licencia.");

            if (!LicenseService.TryValidateAndApply(dto.LicenseToken, cfg, out var msg))
                return (false, silent ? string.Empty : msg);

            cfg.Set("licencia_last_cloud_ok_utc", DateTime.UtcNow.ToString("O"));
            if (dto.NumberOfBoxes > 0)
                cfg.Set("licencia_number_of_boxes", dto.NumberOfBoxes.ToString());
            GrunflexPOS2.App.LicenseState.RefreshFromStores();
            return (true, "Licencia sincronizada con el servidor.");
        }
        catch (Exception ex)
        {
            return (false, silent ? string.Empty : ex.GetBaseException().Message);
        }
    }

    /// <summary>Consulta estado sin emitir token (para diagnóstico).</summary>
    public static async Task<LicensingServerStatus?> TryGetServerStatusAsync(CancellationToken cancellationToken = default)
    {
        var cfg = new ConfiguracionService();
        var aid = cfg.Get("licencia_activation_id")?.Trim();
        if (string.IsNullOrWhiteSpace(aid))
            return null;

        var api = AppConfig.Cargar();
        var baseUri = api.ApiBaseUrl.TrimEnd('/') + "/";

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(baseUri), Timeout = TimeSpan.FromSeconds(20) };
            using var resp = await client.GetAsync(
                    $"api/licensing/status?activationId={Uri.EscapeDataString(aid)}",
                    cancellationToken)
                .ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new LicensingServerStatus { HttpStatus = (int)resp.StatusCode, RawBody = text };

            var dto = JsonSerializer.Deserialize<LicensingStatusDto>(text, JsonOpts);
            if (dto == null)
                return new LicensingServerStatus { RawBody = text };

            return new LicensingServerStatus
            {
                Found = dto.Found,
                Expired = dto.Expired,
                ExpUtc = dto.ExpUtc,
                Multicaja = dto.Multicaja,
                OnlineSupport = dto.OnlineSupport,
                CloudBackup = dto.CloudBackup,
                PrioritySupport = dto.PrioritySupport
            };
        }
        catch (Exception ex)
        {
            return new LicensingServerStatus { Error = ex.GetBaseException().Message };
        }
    }

    private static async Task ApplyStatusIfExpiredAsync(
        HttpClient client,
        string activationId,
        ConfiguracionService cfg,
        CancellationToken cancellationToken)
    {
        try
        {
            using var resp = await client.GetAsync(
                    $"api/licensing/status?activationId={Uri.EscapeDataString(activationId)}",
                    cancellationToken)
                .ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return;

            var dto = JsonSerializer.Deserialize<LicensingStatusDto>(text, JsonOpts);
            if (dto is { Found: true, Expired: true })
                ClearLicenseAndNotify(cfg);
        }
        catch
        {
            /* ignorar */
        }
    }

    private static void ClearLicenseAndNotify(ConfiguracionService cfg)
    {
        LicenseService.ClearStoredLicense(cfg);
        GrunflexPOS2.App.LicenseState.RefreshFromStores();
    }

    private sealed class LicensingTokenDto
    {
        public string LicenseToken { get; set; } = string.Empty;

        public int NumberOfBoxes { get; set; }
    }

    private sealed class LicensingStatusDto
    {
        public bool Found { get; set; }

        public bool Expired { get; set; }

        public DateTime ExpUtc { get; set; }

        public bool Multicaja { get; set; }

        public bool OnlineSupport { get; set; }

        public bool CloudBackup { get; set; }

        public bool PrioritySupport { get; set; }

        public int NumberOfBoxes { get; set; }
    }
}

public sealed class LicensingServerStatus
{
    public int? HttpStatus { get; init; }

    public string? RawBody { get; init; }

    public string? Error { get; init; }

    public bool Found { get; init; }

    public bool Expired { get; init; }

    public DateTime ExpUtc { get; init; }

    public bool Multicaja { get; init; }

    public bool OnlineSupport { get; init; }

    public bool CloudBackup { get; init; }

    public bool PrioritySupport { get; init; }
}
