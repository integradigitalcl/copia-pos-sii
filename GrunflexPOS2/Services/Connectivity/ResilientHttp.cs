using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace GrunflexPOS2.Services.Connectivity;

/// <summary>
/// Helper de reintentos exponenciales para llamadas HTTP. Diseñado para envolver
/// llamadas a la API del POS sin que la UI se cuelgue ante un timeout.
///
/// Política por defecto:
///   - 4 intentos totales (incluye el inicial).
///   - Espera: 250ms, 750ms, 2.0s entre intentos.
///   - Reintenta solo en errores transitorios: HttpRequestException, TaskCanceledException,
///     SocketException, IOException, y respuestas 5xx / 408 / 429.
///
/// NUNCA bloquea más que el total de timeouts; respeta cancellation.
/// </summary>
public static class ResilientHttp
{
    private static readonly TimeSpan[] DefaultDelays =
    {
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromSeconds(2)
    };

    public static async Task<HttpResponseMessage?> SendWithRetryAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> sendFunc,
        CancellationToken ct = default,
        TimeSpan[]? delays = null)
    {
        var schedule = delays ?? DefaultDelays;
        Exception? lastEx = null;
        HttpResponseMessage? response = null;

        for (var attempt = 0; attempt <= schedule.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                response?.Dispose();
                response = await sendFunc(ct).ConfigureAwait(false);
                if (IsTransientResponse(response))
                {
                    if (attempt < schedule.Length)
                    {
                        await Task.Delay(schedule[attempt], ct).ConfigureAwait(false);
                        continue;
                    }
                }
                return response; // OK or non-transient failure
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // timeout interno del HttpClient: reintenta
                lastEx = new TimeoutException("HTTP timeout");
            }
            catch (OperationCanceledException)
            {
                throw; // cancelado por el caller, salimos
            }
            catch (Exception ex) when (IsTransientException(ex))
            {
                lastEx = ex;
            }

            if (attempt < schedule.Length)
            {
                try { await Task.Delay(schedule[attempt], ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }
        }

        if (response != null) return response;
        // Devolvemos null para que el caller decida (typical: fallback offline).
        // No relanzamos para no romper UI; lastEx queda accesible via log.
        if (lastEx != null) PosDiagnostics.Log("ResilientHttp: agotó reintentos: " + lastEx.Message);
        return null;
    }

    private static bool IsTransientResponse(HttpResponseMessage resp)
    {
        if (resp == null) return false;
        var code = (int)resp.StatusCode;
        return code == 408 || code == 429 || (code >= 500 && code < 600);
    }

    private static bool IsTransientException(Exception ex)
    {
        return ex is HttpRequestException
            || ex is SocketException
            || ex is System.IO.IOException
            || ex is TimeoutException;
    }
}
