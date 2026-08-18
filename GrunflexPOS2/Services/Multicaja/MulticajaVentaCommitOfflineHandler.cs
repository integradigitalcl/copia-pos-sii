using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grunflex.Idempotency;
using GrunflexPOS2.Services.Offline;
namespace GrunflexPOS2.Services.Multicaja;

/// <summary>
/// Reenvía <see cref="MulticajaVentaCommitRequest"/> guardados en <see cref="OfflineQueue"/> hacia la API central.
/// Idempotencia: fuerza <c>RequestId == item.Id</c> para alinear con <c>MulticajaVentaIdempotency</c> en servidor.
/// </summary>
public sealed class MulticajaVentaCommitOfflineHandler : IOfflineHandler
{
    private static readonly JsonSerializerOptions JsonRead = new() { PropertyNameCaseInsensitive = true };

    public async Task<(bool ok, string? error)> TryHandleAsync(OfflineQueueItem item, CancellationToken ct)
    {
        MulticajaVentaCommitRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<MulticajaVentaCommitRequest>(item.PayloadJson, JsonRead);
        }
        catch (System.Text.Json.JsonException jx)
        {
            return (false, "json:" + jx.Message);
        }

        if (req == null)
            return (false, "payload null");

        if (!string.IsNullOrEmpty(item.PayloadSha256))
        {
            var hash = IdempotencyPayloadHasher.HashJson(item.PayloadJson);
            if (!string.Equals(item.PayloadSha256, hash, StringComparison.OrdinalIgnoreCase))
            {
                PosDiagnostics.Log($"multicaja.replay hash_mismatch id={item.Id[..8]}");
                return (false, "payload hash mismatch");
            }
        }

        req.RequestId = item.Id;
        var resp = await MulticajaOperacionesClient.CommitVentaAsync(req, ct).ConfigureAwait(false);
        if (resp == null)
            return (false, "sin respuesta API");

        if (resp.Ok)
        {
            PosDiagnostics.Log($"multicaja.replay venta ok ticket={resp.NumeroTicket} id={item.Id[..8]}");
            return (true, null);
        }

        // Errores de negocio (stock, sesión): no reintentar infinitamente — la cola seguirá fallando
        // hasta intervención; el operador debe corregir o purgar el archivo en queue/.
        return (false, $"{resp.ErrorCode ?? "?"}:{resp.Error ?? "error"}");
    }
}
