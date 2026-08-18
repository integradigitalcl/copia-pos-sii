using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GrunflexPOS2.Services.Offline;

namespace GrunflexPOS2.Services.Multicaja;

public sealed class MulticajaAnulacionOfflineHandler : IOfflineHandler
{
    private static readonly JsonSerializerOptions JsonRead = new() { PropertyNameCaseInsensitive = true };

    public async Task<(bool ok, string? error)> TryHandleAsync(OfflineQueueItem item, CancellationToken ct)
    {
        MulticajaAnularVentaRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<MulticajaAnularVentaRequest>(item.PayloadJson, JsonRead);
        }
        catch (JsonException jx)
        {
            return (false, "json:" + jx.Message);
        }

        if (req == null)
            return (false, "payload null");

        req.RequestId = item.Id;
        var resp = await MulticajaOperacionesClient.AnularVentaAsync(req, ct).ConfigureAwait(false);
        if (resp == null)
            return (false, "sin respuesta API");
        if (resp.Ok)
        {
            PosDiagnostics.Log($"multicaja.replay anulacion ok ticket={resp.NumeroTicket} id={item.Id[..8]}");
            return (true, null);
        }

        return (false, $"{resp.ErrorCode ?? "?"}:{resp.Error ?? "error"}");
    }
}

public sealed class MulticajaDevolucionOfflineHandler : IOfflineHandler
{
    private static readonly JsonSerializerOptions JsonRead = new() { PropertyNameCaseInsensitive = true };

    public async Task<(bool ok, string? error)> TryHandleAsync(OfflineQueueItem item, CancellationToken ct)
    {
        MulticajaDevolucionLineaRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<MulticajaDevolucionLineaRequest>(item.PayloadJson, JsonRead);
        }
        catch (JsonException jx)
        {
            return (false, "json:" + jx.Message);
        }

        if (req == null)
            return (false, "payload null");

        req.RequestId = item.Id;
        var resp = await MulticajaOperacionesClient.DevolverLineaAsync(req, ct).ConfigureAwait(false);
        if (resp == null)
            return (false, "sin respuesta API");
        if (resp.Ok)
        {
            PosDiagnostics.Log($"multicaja.replay devolucion ok ticket={resp.NumeroTicket} id={item.Id[..8]}");
            return (true, null);
        }

        return (false, $"{resp.ErrorCode ?? "?"}:{resp.Error ?? "error"}");
    }
}

public sealed class MulticajaCierreOfflineHandler : IOfflineHandler
{
    private static readonly JsonSerializerOptions JsonRead = new() { PropertyNameCaseInsensitive = true };

    public async Task<(bool ok, string? error)> TryHandleAsync(OfflineQueueItem item, CancellationToken ct)
    {
        MulticajaCierreCajaRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<MulticajaCierreCajaRequest>(item.PayloadJson, JsonRead);
        }
        catch (JsonException jx)
        {
            return (false, "json:" + jx.Message);
        }

        if (req == null)
            return (false, "payload null");

        req.RequestId = item.Id;
        var resp = await MulticajaOperacionesClient.CerrarSesionAsync(req, ct).ConfigureAwait(false);
        if (resp == null)
            return (false, "sin respuesta API");
        if (resp.Ok)
        {
            PosDiagnostics.Log($"multicaja.replay cierre ok id={item.Id[..8]} dif={resp.Diferencia}");
            return (true, null);
        }

        return (false, $"{resp.ErrorCode ?? "?"}:{resp.Error ?? "error"}");
    }
}

public sealed class MulticajaMovimientoCajaOfflineHandler : IOfflineHandler
{
    private static readonly JsonSerializerOptions JsonRead = new() { PropertyNameCaseInsensitive = true };

    public async Task<(bool ok, string? error)> TryHandleAsync(OfflineQueueItem item, CancellationToken ct)
    {
        MulticajaMovimientoCajaRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<MulticajaMovimientoCajaRequest>(item.PayloadJson, JsonRead);
        }
        catch (JsonException jx)
        {
            return (false, "json:" + jx.Message);
        }

        if (req == null)
            return (false, "payload null");

        req.RequestId = item.Id;
        var resp = await MulticajaOperacionesClient.RegistrarMovimientoCajaAsync(req, ct).ConfigureAwait(false);
        if (resp == null)
            return (false, "sin respuesta API");
        if (resp.Ok)
        {
            PosDiagnostics.Log($"multicaja.replay movimiento-caja ok tipo={resp.Tipo} id={item.Id[..8]}");
            return (true, null);
        }

        return (false, $"{resp.ErrorCode ?? "?"}:{resp.Error ?? "error"}");
    }
}
