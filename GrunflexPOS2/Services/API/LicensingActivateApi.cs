using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using GrunflexPOS2.Data;

namespace GrunflexPOS2.Services.API;

public static class LicensingActivateApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<(bool Ok, string? Token, string Message)> TryActivateAsync(
        string activationId,
        string machineName,
        CancellationToken cancellationToken = default)
    {
        var cfg = AppConfig.Cargar();
        var baseUrl = cfg.ApiBaseUrl.TrimEnd('/') + "/";

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(25) };
            var body = new { activationId, machineName };
            using var response = await client.PostAsJsonAsync("api/licensing/activate", body, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (false, null, $"Servidor: {(int)response.StatusCode} {text}");

            var dto = JsonSerializer.Deserialize<LicensingTokenDto>(text, JsonOptions);
            return string.IsNullOrWhiteSpace(dto?.LicenseToken)
                ? (false, null, "Respuesta sin token.")
                : (true, dto.LicenseToken.Trim(), "OK");
        }
        catch (Exception ex)
        {
            return (false, null, ex.GetBaseException().Message);
        }
    }

    private sealed class LicensingTokenDto
    {
        public string? LicenseToken { get; set; }
    }
}
