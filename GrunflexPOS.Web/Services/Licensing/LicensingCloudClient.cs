using System.Net.Http.Json;
using System.Text.Json;

namespace GrunflexPOS.Web.Services.Licensing;

public sealed class LicensingCloudClient(
    IHttpClientFactory httpClientFactory,
    LocalPosStore store,
    WebLicenseService licenseService,
    WebLicenseState licenseState,
    IConfiguration configuration,
    ILogger<LicensingCloudClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<(bool Ok, string Message)> ActivateAsync(
        string activationId, CancellationToken cancellationToken = default)
    {
        activationId = activationId.Trim();
        if (string.IsNullOrWhiteSpace(activationId))
            return (false, "Ingrese un ActivationId.");

        var baseUrl = await ResolveApiBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return (false, "No hay URL de API configurada para licencias.");

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(25);
            var body = new { activationId, machineName = Environment.MachineName };
            using var response = await client.PostAsJsonAsync(
                $"{baseUrl.TrimEnd('/')}/api/licensing/activate", body, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (false, $"Servidor: {(int)response.StatusCode} {text}");

            var dto = JsonSerializer.Deserialize<LicensingTokenDto>(text, JsonOptions);
            if (string.IsNullOrWhiteSpace(dto?.LicenseToken))
                return (false, "Respuesta sin token de licencia.");

            return await ApplyTokenAsync(dto.LicenseToken, dto.NumberOfBoxes, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fallo activación de licencia");
            return (false, ex.GetBaseException().Message);
        }
    }

    public async Task<(bool Ok, string Message)> RefreshAsync(
        bool silent, CancellationToken cancellationToken = default)
    {
        var activationId = (await store.GetSettingAsync("licencia_activation_id", cancellationToken: cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(activationId))
            return (false, silent ? string.Empty : "No hay ActivationId en la configuración local.");

        var baseUrl = await ResolveApiBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return (false, silent ? string.Empty : "No hay URL de API configurada para licencias.");

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(25);
            var body = new { activationId, machineName = Environment.MachineName };
            using var response = await client.PostAsJsonAsync(
                $"{baseUrl.TrimEnd('/')}/api/licensing/refresh", body, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (text.Contains("vencida", StringComparison.OrdinalIgnoreCase))
                    await licenseService.ClearStoredLicenseAsync(cancellationToken);
                return (false, silent ? string.Empty : $"Servidor: {(int)response.StatusCode} {text}");
            }

            var dto = JsonSerializer.Deserialize<LicensingTokenDto>(text, JsonOptions);
            if (string.IsNullOrWhiteSpace(dto?.LicenseToken))
                return (false, silent ? string.Empty : "Respuesta sin token de licencia.");

            var applied = await ApplyTokenAsync(dto.LicenseToken, dto.NumberOfBoxes, cancellationToken);
            if (applied.Ok)
                await licenseService.MarkCloudSyncOkAsync(cancellationToken);
            return applied;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Fallo refresh de licencia");
            return (false, silent ? string.Empty : ex.GetBaseException().Message);
        }
    }

    public async Task<(bool Ok, string Message)> ApplyTokenAsync(
        string token, int numberOfBoxes, CancellationToken cancellationToken)
    {
        var result = await licenseService.TryValidateAndApplyAsync(token, cancellationToken);
        if (!result.Success)
            return result;

        if (numberOfBoxes > 0)
            await store.SetSettingAsync("licencia_number_of_boxes", numberOfBoxes.ToString(), cancellationToken);

        await licenseState.RefreshAsync(cancellationToken);
        return (true, "Licencia aplicada correctamente.");
    }

    private async Task<string?> ResolveApiBaseUrlAsync(CancellationToken cancellationToken)
    {
        var configured = configuration["Licensing:ApiBaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        var multicaja = await store.GetSettingAsync("multicaja_api_url", cancellationToken: cancellationToken);
        if (!string.IsNullOrWhiteSpace(multicaja))
            return multicaja.Trim();

        return configuration["Multicaja:ApiBaseUrl"];
    }

    private sealed class LicensingTokenDto
    {
        public string LicenseToken { get; set; } = string.Empty;
        public int NumberOfBoxes { get; set; }
    }
}
