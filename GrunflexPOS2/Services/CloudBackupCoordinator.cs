using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using GrunflexPOS2.Data;

namespace GrunflexPOS2.Services;

/// <summary>Sube el SQLite local al API cuando la licencia incluye CloudBackup.</summary>
public static class CloudBackupCoordinator
{
    private static readonly object Gate = new();
    private static bool _started;
    private static System.Windows.Threading.DispatcherTimer? _periodic;

    public static void TryStartMainWindow()
    {
        lock (Gate)
        {
            if (_started)
                return;
            _started = true;

            var once = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
            once.Tick += (_, _) =>
            {
                once.Stop();
                _ = RunUploadSafeAsync();
            };
            once.Start();

            _periodic = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(8) };
            _periodic.Tick += (_, _) => _ = RunUploadSafeAsync();
            _periodic.Start();
        }
    }

    private static async Task RunUploadSafeAsync()
    {
        try
        {
            await TryUploadOnceAsync().ConfigureAwait(true);
        }
        catch
        {
            /* ignorar en segundo plano */
        }
    }

    public static async Task<(bool Ok, string Message)> TryUploadOnceAsync(CancellationToken cancellationToken = default)
    {
        App.LicenseState.RefreshFromStores();
        if (!App.LicenseState.CloudBackup)
            return (false, "Módulo CloudBackup no activo.");

        var cfg = new ConfiguracionService();
        var activation = cfg.Get("licencia_activation_id")?.Trim();
        if (string.IsNullOrWhiteSpace(activation))
            return (false, "No hay ActivationId en la licencia (active en línea o use licencia GFv2 con ActivationId).");

        LocalDatabasePaths.EnsureDataDirectoryExists();
        var dbPath = LocalDatabasePaths.DatabaseFilePath;
        if (!File.Exists(dbPath))
            return (false, "No se encontró la base local.");

        var zipPath = Path.Combine(Path.GetTempPath(), $"grunflex-pos-backup-{Guid.NewGuid():N}.zip");
        try
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(dbPath, "grunflex.db", CompressionLevel.Optimal);
                var manifest = zip.CreateEntry("manifest.json");
                await using var mw = new StreamWriter(manifest.Open());
                await mw.WriteAsync(
                    $$"""{"version":1,"machine":"{{Environment.MachineName}}","utc":"{{DateTime.UtcNow:O}}"}""").ConfigureAwait(false);
            }

            var api = AppConfig.Cargar();
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var form = new MultipartFormDataContent();
            await using var fs = File.OpenRead(zipPath);
            var streamContent = new StreamContent(fs);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(streamContent, "file", Path.GetFileName(zipPath));
            using var req = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(new Uri(api.ApiBaseUrl.TrimEnd('/') + "/", UriKind.Absolute), "api/backups/upload"))
            {
                Content = form
            };
            req.Headers.TryAddWithoutValidation("X-Tenant-Activation-Id", activation);

            using var resp = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var snippet = body.Length > 400 ? body[..400] + "…" : body;
                cfg.Set("ultimo_backup_nube_error", $"{(int)resp.StatusCode}: {snippet}");
                cfg.Set("ultimo_backup_nube_error_utc", DateTime.UtcNow.ToString("O"));
                return (false, $"Error {(int)resp.StatusCode}: {body}");
            }

            cfg.Set("ultimo_backup_nube_error", string.Empty);
            cfg.Set("ultimo_backup_nube_error_utc", string.Empty);
            cfg.Set("ultimo_backup_nube_utc", DateTime.UtcNow.ToString("O"));
            return (true, "Respaldo subido.");
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
}
