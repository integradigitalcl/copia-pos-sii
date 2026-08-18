using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using GrunflexPOS2.Data;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Services.API;

public static class BackupsApiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static async Task<(bool Ok, IReadOnlyList<BackupListRow> Items, string Message)> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var aid = new ConfiguracionService().Get("licencia_activation_id")?.Trim();
        if (string.IsNullOrWhiteSpace(aid))
            return (false, Array.Empty<BackupListRow>(), "No hay ActivationId en la configuración.");

        var api = AppConfig.Cargar();
        try
        {
            using var http = new HttpClient
            {
                BaseAddress = new Uri(api.ApiBaseUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(30)
            };
            using var req = new HttpRequestMessage(HttpMethod.Get, "api/backups");
            req.Headers.TryAddWithoutValidation("X-Tenant-Activation-Id", aid);
            using var resp = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, Array.Empty<BackupListRow>(), $"Error {(int)resp.StatusCode}: {text}");

            var items = JsonSerializer.Deserialize<List<BackupListRow>>(text, JsonOpts) ?? new List<BackupListRow>();
            return (true, items, "OK");
        }
        catch (Exception ex)
        {
            return (false, Array.Empty<BackupListRow>(), ex.GetBaseException().Message);
        }
    }

    public static async Task<(bool Ok, string Message)> DeleteAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var aid = new ConfiguracionService().Get("licencia_activation_id")?.Trim();
        if (string.IsNullOrWhiteSpace(aid))
            return (false, "No hay ActivationId.");

        var api = AppConfig.Cargar();
        try
        {
            using var http = new HttpClient
            {
                BaseAddress = new Uri(api.ApiBaseUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(30)
            };
            var q = Uri.EscapeDataString(relativePath);
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"api/backups?relativePath={q}");
            req.Headers.TryAddWithoutValidation("X-Tenant-Activation-Id", aid);
            using var resp = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, $"Error {(int)resp.StatusCode}: {text}");

            return (true, "Eliminado.");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }
}

public sealed class BackupListRow
{
    public string RelativePath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public DateTime StoredAtUtc { get; set; }
}
