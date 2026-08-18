using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GrunflexPOS2.Data;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Connectivity;
using GrunflexPOS2.Services.Offline;

namespace GrunflexPOS2.Services.Multicaja;

/// <summary>
/// Validación al arranque para terminal API-only: config, secret, health. Logs multicaja.*.
/// </summary>
public static class MulticajaStartupValidation
{
    public static void Run(AppConfig cfg, ConnectivityMonitor connectivity)
    {
        MulticajaRuntime.BlockMonetaryForInvalidConfig = false;
        MulticajaRuntime.StartupApiUnreachable = false;

        if (!MulticajaRuntime.UseApiOnlyClient)
            return;

        var issues = new List<string>();

        PosDiagnostics.Log(
            $"multicaja.config useApiOnly=true apiBaseUrl={cfg.ApiBaseUrl} cajaId={cfg.CajaId} requireSecret={cfg.MulticajaRequireSharedSecret} " +
            $"hasSecret={!string.IsNullOrWhiteSpace(cfg.MulticajaSharedSecret)} blockCriticalOffline={cfg.MulticajaBlockCriticalWhenOffline} " +
            $"enqueueCriticalOffline={cfg.MulticajaEnqueueCriticalWhenOffline} uncDb={cfg.TieneConexionUnc}");

        try
        {
            if (string.IsNullOrWhiteSpace(cfg.ApiBaseUrl))
                issues.Add("Api:BaseUrl vacía.");
            else if (!Uri.TryCreate(cfg.ApiBaseUrl.Trim(), UriKind.Absolute, out var apiUri))
                issues.Add("Api:BaseUrl no es una URI absoluta válida.");
            else if (cfg.EsCajaAdicional && apiUri.IsLoopback)
            {
                issues.Add($"Api:BaseUrl no puede ser localhost en caja adicional ({cfg.ApiBaseUrl}).");
                MulticajaRuntime.BlockMonetaryForInvalidConfig = true;
            }

            if (cfg.EsCajaAdicional && cfg.TieneConexionUnc)
            {
                issues.Add("ConnectionStrings con UNC/SMB no permitido en modo API-only.");
                MulticajaRuntime.BlockMonetaryForInvalidConfig = true;
            }

            if (cfg.MulticajaRequireSharedSecret && string.IsNullOrWhiteSpace(cfg.MulticajaSharedSecret))
                issues.Add("Multicaja:RequireSharedSecret activo pero Multicaja:SharedSecret está vacío.");

            if (!Guid.TryParse((cfg.CajaId ?? "").Trim(), out _))
                issues.Add("CajaId debe ser un GUID válido.");

            MulticajaRuntime.BlockMonetaryForInvalidConfig = issues.Count > 0;

            try
            {
                var qd = OfflineQueue.QueueDirectory;
                Directory.CreateDirectory(qd);
                var tpath = Path.Combine(qd, ".write_probe.tmp");
                File.WriteAllText(tpath, "ok");
                File.Delete(tpath);
            }
            catch (Exception ex)
            {
                issues.Add("Cola offline: sin permiso de escritura (" + ex.Message + ").");
                MulticajaRuntime.BlockMonetaryForInvalidConfig = true;
            }

            if (Guid.TryParse((cfg.CajaId ?? "").Trim(), out var cajaProbe))
            {
                try
                {
                    var st = Task.Run(() => MulticajaOperacionesClient.GetCajaExistsHttpStatusAsync(cajaProbe))
                        .GetAwaiter().GetResult();
                    if (st == HttpStatusCode.Unauthorized)
                    {
                        issues.Add("API multicaja respondió 401: revisá Multicaja:SharedSecret.");
                        MulticajaRuntime.BlockMonetaryForInvalidConfig = true;
                    }
                    else if (st == HttpStatusCode.NotFound)
                    {
                        issues.Add("CajaId no existe en el servidor (404).");
                        MulticajaRuntime.BlockMonetaryForInvalidConfig = true;
                    }
                    else if (st == HttpStatusCode.ServiceUnavailable)
                    {
                        issues.Add("API en 503 (p.ej. RequireSharedSecret sin secreto en servidor).");
                        MulticajaRuntime.BlockMonetaryForInvalidConfig = true;
                    }
                    else if (st != HttpStatusCode.NoContent)
                        issues.Add($"Endpoint multicaja cajas/exists respondió HTTP {(int)st}.");
                }
                catch (Exception ex)
                {
                    issues.Add("No se pudo consultar endpoint multicaja: " + ex.Message);
                }
            }

            var healthOk = false;
            try
            {
                healthOk = connectivity.CheckOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                issues.Add("Health check: " + ex.Message);
            }

            if (!healthOk)
            {
                MulticajaRuntime.StartupApiUnreachable = true;
                issues.Add("La API no respondió a /health/live al arranque.");
                if (cfg.MulticajaBlockCriticalWhenOffline)
                    MulticajaRuntime.BlockMonetaryForInvalidConfig = true;
            }

            var y = DateTime.UtcNow.Year;
            if (y < 2020 || y > 2100)
                issues.Add("Reloj del sistema (UTC) fuera de rango esperado; verificar NTP.");

            PosDiagnostics.Log(issues.Count == 0
                ? "multicaja.validation ok"
                : "multicaja.validation issues=" + string.Join(" | ", issues));

            PosDiagnostics.Log(
                $"multicaja.startup blockMonetary={MulticajaRuntime.BlockMonetaryForInvalidConfig} apiUnreachableAtStartup={MulticajaRuntime.StartupApiUnreachable}");

            MulticajaRiskScanner.Scan("startup_validation");
            MulticajaDiagnostics.WriteStartupDump();

            if (issues.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Modo multicaja API-only: revisar configuración o conectividad.");
                sb.AppendLine();
                foreach (var i in issues)
                    sb.AppendLine("• " + i);
                MessageBox.Show(sb.ToString(), "Grunflex POS — multicaja", MessageBoxButton.OK,
                    MulticajaRuntime.BlockMonetaryForInvalidConfig ? MessageBoxImage.Stop : MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("multicaja.validation error", ex);
        }
    }
}
