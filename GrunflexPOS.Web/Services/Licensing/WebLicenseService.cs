using System.Text.Json;
using Grunflex.Licensing;

namespace GrunflexPOS.Web.Services.Licensing;

public sealed class WebLicenseService(
    LocalPosStore store,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<WebLicenseService> logger)
{
    public async Task<LicenseStatus> EvaluateStoredAsync(CancellationToken cancellationToken = default)
    {
        var status = await EvaluateStoredCoreAsync(cancellationToken);
        return status;
    }

    public async Task<(bool Success, string Message)> TryValidateAndApplyAsync(
        string licenseToken, CancellationToken cancellationToken = default)
    {
        if (!TryValidate(licenseToken, out var payload, out var message))
            return (false, message);

        await PersistPayloadAsync(licenseToken.Trim(), payload, cancellationToken);
        return (true, message);
    }

    public async Task ClearStoredLicenseAsync(CancellationToken cancellationToken = default)
    {
        await store.SetSettingsAsync(new Dictionary<string, string>
        {
            ["licencia_key"] = string.Empty,
            ["licencia_activation_id"] = string.Empty,
            ["licencia_exp_utc"] = string.Empty,
            ["licencia_validada_utc"] = string.Empty,
            ["licencia_last_cloud_ok_utc"] = string.Empty,
            ["licencia_offline_grace_days"] = string.Empty,
            ["licencia_number_of_boxes"] = string.Empty,
            ["licencia_estado"] = "Sin licencia",
            ["licencia_vencimiento"] = string.Empty
        }, cancellationToken);
    }

    public async Task MarkCloudSyncOkAsync(CancellationToken cancellationToken = default) =>
        await store.SetSettingAsync("licencia_last_cloud_ok_utc", DateTime.UtcNow.ToString("O"), cancellationToken);

    public int ResolveOfflineGraceDays(int payloadDays)
    {
        if (payloadDays > 0)
            return payloadDays;

        var configured = configuration["Licensing:OfflineGraceDays"];
        if (int.TryParse(configured, out var days) && days > 0)
            return days;

        return GrunflexLicenseDefaults.OfflineGraceDays;
    }

    public async Task<LicenseStatus> EvaluateStoredCoreAsync(CancellationToken cancellationToken = default)
    {
        var license = (await store.GetSettingAsync("licencia_key", cancellationToken: cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(license))
        {
            await UpdateDisplayStatusAsync("Sin licencia", string.Empty, cancellationToken);
            return LicenseStatus.Missing;
        }

        if (TryParseExpUtc(await store.GetSettingAsync("licencia_exp_utc", cancellationToken: cancellationToken), out var expStored) &&
            expStored <= DateTime.UtcNow)
        {
            await UpdateDisplayStatusAsync("Licencia vencida", expStored.ToLocalTime().ToString("dd/MM/yyyy"), cancellationToken);
            return LicenseStatus.Expired;
        }

        if (TryValidate(license, out var payload, out _))
        {
            await PersistPayloadAsync(license, payload, cancellationToken);
            return LicenseStatus.Valid;
        }

        await UpdateDisplayStatusAsync("Licencia inválida", string.Empty, cancellationToken);
        return LicenseStatus.Invalid;
    }

    private async Task PersistPayloadAsync(
        string licenseToken, GrunflexLicensePayload payload, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>
        {
            ["licencia_key"] = licenseToken.Trim(),
            ["licencia_exp_utc"] = payload.ExpUtc.ToString("O"),
            ["licencia_estado"] = $"Activa · {payload.Customer}".Trim(' ', '·'),
            ["licencia_vencimiento"] = payload.ExpUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
        };

        if (!string.IsNullOrWhiteSpace(payload.ActivationId))
            values["licencia_activation_id"] = payload.ActivationId.Trim();

        var validated = await store.GetSettingAsync("licencia_validada_utc", cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(validated))
            values["licencia_validada_utc"] = DateTime.UtcNow.ToString("O");

        values["licencia_offline_grace_days"] = payload.OfflineGraceDays > 0
            ? payload.OfflineGraceDays.ToString()
            : string.Empty;

        if (payload.NumberOfBoxes > 0)
            values["licencia_number_of_boxes"] = payload.NumberOfBoxes.ToString();

        values["licencia_multicaja"] = payload.Multicaja ? "true" : "false";
        values["licencia_online_support"] = payload.OnlineSupport ? "true" : "false";
        values["licencia_cloud_backup"] = payload.CloudBackup ? "true" : "false";
        values["licencia_priority_support"] = payload.PrioritySupport ? "true" : "false";

        await store.SetSettingsAsync(values, cancellationToken);
    }

    private async Task UpdateDisplayStatusAsync(string status, string expiry, CancellationToken cancellationToken) =>
        await store.SetSettingsAsync(new Dictionary<string, string>
        {
            ["licencia_estado"] = status,
            ["licencia_vencimiento"] = expiry
        }, cancellationToken);

    private bool TryValidate(string licenseToken, out GrunflexLicensePayload payload, out string message)
    {
        payload = new GrunflexLicensePayload();
        if (string.IsNullOrWhiteSpace(licenseToken))
        {
            message = "Licencia vacía.";
            return false;
        }

        var token = licenseToken.Trim();
        if (!token.StartsWith(GrunflexLicenseCodec.VersionPrefix + ".", StringComparison.Ordinal))
        {
            message = "Solo se admiten licencias GFv2 en el POS web.";
            return false;
        }

        var pem = TryLoadPublicKeyPem();
        if (string.IsNullOrWhiteSpace(pem))
            pem = TryDownloadPublicKeyPem();

        if (string.IsNullOrWhiteSpace(pem))
        {
            message = "No hay clave pública local para validar GFv2. Configure Licensing:PublicKeyPem o ejecute la API central.";
            return false;
        }

        try
        {
            using var rsa = GrunflexLicenseCodec.ImportPublicKeyFromPem(pem);
            if (!GrunflexLicenseCodec.TryVerifyV2(token, rsa, out var parsed, out message) || parsed is null)
                return false;

            payload = parsed;
            return ValidatePayloadRules(payload, out message);
        }
        catch (Exception ex)
        {
            message = "No se pudo cargar la clave pública: " + ex.Message;
            return false;
        }
    }

    private static bool ValidatePayloadRules(GrunflexLicensePayload payload, out string message)
    {
        if (payload.ExpUtc <= DateTime.UtcNow)
        {
            message = "La licencia está vencida.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(payload.Machine) &&
            !string.Equals(payload.Machine.Trim(), Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            message = "Esta licencia no corresponde a este equipo.";
            return false;
        }

        if (!payload.Multicaja && !payload.OnlineSupport && !payload.CloudBackup && !payload.PrioritySupport)
        {
            message = "La licencia no habilita módulos.";
            return false;
        }

        message = "Licencia válida.";
        return true;
    }

    private string? TryLoadPublicKeyPem()
    {
        var embedded = configuration["Licensing:PublicKeyPem"];
        if (!string.IsNullOrWhiteSpace(embedded))
            return embedded.Trim();

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GrunflexPOS",
            "licensing-public.pem");
        if (File.Exists(path))
        {
            try
            {
                return File.ReadAllText(path).Trim();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "No se pudo leer licensing-public.pem");
            }
        }

        return null;
    }

    private string? TryDownloadPublicKeyPem()
    {
        try
        {
            var baseUrl = ResolveApiBaseUrl();
            if (string.IsNullOrWhiteSpace(baseUrl))
                return null;

            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            using var response = client.GetAsync($"{baseUrl.TrimEnd('/')}/api/licensing/public-key")
                .GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return null;

            var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("publicKeyPem", out var pemNode))
                return null;

            var pem = pemNode.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(pem))
                return null;

            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GrunflexPOS",
                "licensing-public.pem");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, pem);
            return pem;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No se pudo descargar la clave pública de licencias");
            return null;
        }
    }

    private string? ResolveApiBaseUrl() =>
        configuration["Licensing:ApiBaseUrl"]
        ?? configuration["Multicaja:ApiBaseUrl"];

    private static bool TryParseExpUtc(string? raw, out DateTime expUtc)
    {
        expUtc = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (!DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            return false;

        expUtc = parsed.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : parsed.ToUniversalTime();
        return true;
    }
}
