using System.IO.Compression;
using System.Net.Http.Headers;

namespace GrunflexPOS.Web.Services.Licensing;

public sealed class CloudBackupService(
    LocalPosStore store,
    WebLicenseState licenseState,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<CloudBackupService> logger)
{
    public async Task<(bool Ok, string Message)> TryUploadOnceAsync(CancellationToken cancellationToken = default)
    {
        await licenseState.RefreshAsync(cancellationToken);
        if (!licenseState.CloudBackup)
            return (false, "Módulo CloudBackup no activo en la licencia.");

        var activationId = (await store.GetSettingAsync("licencia_activation_id", cancellationToken: cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(activationId))
            return (false, "No hay ActivationId en la licencia.");

        var databasePath = ResolveDatabasePath();
        if (!File.Exists(databasePath))
            return (false, "No se encontró la base SQLite local.");

        var zipPath = Path.Combine(Path.GetTempPath(), $"grunflex-web-backup-{Guid.NewGuid():N}.zip");
        try
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(databasePath, Path.GetFileName(databasePath), CompressionLevel.Optimal);
                var manifest = zip.CreateEntry("manifest.json");
                using var writer = new StreamWriter(manifest.Open());
                writer.Write(
                    "{\"version\":1,\"machine\":\"" + Environment.MachineName +
                    "\",\"utc\":\"" + DateTime.UtcNow.ToString("O") +
                    "\",\"source\":\"GrunflexPOS.Web\"}");
            }

            var baseUrl = await ResolveApiBaseUrlAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(baseUrl))
                return (false, "No hay URL de API configurada para respaldos.");

            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(5);
            await using var fileStream = File.OpenRead(zipPath);
            using var form = new MultipartFormDataContent();
            var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(streamContent, "file", Path.GetFileName(zipPath));

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/backups/upload")
            {
                Content = form
            };
            request.Headers.TryAddWithoutValidation("X-Tenant-Activation-Id", activationId);

            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var snippet = body.Length > 400 ? body[..400] + "…" : body;
                await store.SetSettingAsync("ultimo_backup_nube_error", $"{(int)response.StatusCode}: {snippet}", cancellationToken);
                await store.SetSettingAsync("ultimo_backup_nube_error_utc", DateTime.UtcNow.ToString("O"), cancellationToken);
                return (false, $"Error {(int)response.StatusCode}: {body}");
            }

            await store.SetSettingAsync("ultimo_backup_nube_error", string.Empty, cancellationToken);
            await store.SetSettingAsync("ultimo_backup_nube_error_utc", string.Empty, cancellationToken);
            await store.SetSettingAsync("ultimo_backup_nube_utc", DateTime.UtcNow.ToString("O"), cancellationToken);
            logger.LogInformation("Respaldo en nube subido para {ActivationId}", activationId);
            return (true, "Respaldo subido correctamente.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fallo respaldo en nube");
            return (false, ex.GetBaseException().Message);
        }
        finally
        {
            try
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    public async Task<string> GetLastBackupStatusAsync(CancellationToken cancellationToken = default)
    {
        var okUtc = await store.GetSettingAsync("ultimo_backup_nube_utc", cancellationToken: cancellationToken);
        if (!string.IsNullOrWhiteSpace(okUtc) &&
            DateTime.TryParse(okUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            return $"Último respaldo: {parsed.ToLocalTime():dd/MM/yyyy HH:mm}";

        var err = await store.GetSettingAsync("ultimo_backup_nube_error", cancellationToken: cancellationToken);
        return string.IsNullOrWhiteSpace(err) ? "Sin respaldos en nube aún." : $"Error: {err}";
    }

    private string ResolveDatabasePath()
    {
        var configured = configuration["Data:DatabasePath"];
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GrunflexPOS", "grunflex-pos.db")
            : configured;
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
}
