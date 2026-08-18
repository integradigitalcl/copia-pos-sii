using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Services.Multicaja.Bootstrap;

public sealed class ApiReadinessService
{
    public async Task<bool> WaitUntilHealthyAsync(
        string apiBaseUrl,
        TimeSpan maxWait,
        Action<string>? onStatus,
        CancellationToken ct = default)
    {
        var baseUrl = (apiBaseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
            return false;

        var url = baseUrl + "/health/live";
        var deadline = DateTime.UtcNow + maxWait;
        var attempt = 0;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            onStatus?.Invoke($"Conectando con el servidor… (intento {attempt})");

            try
            {
                using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    onStatus?.Invoke("Servidor disponible.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log($"ApiReadiness intento {attempt}: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(3, 1 + attempt / 3)), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        onStatus?.Invoke("El servidor no respondió a tiempo.");
        return false;
    }
}
