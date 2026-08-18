using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Idempotency;

namespace GrunflexPOS.API.Services;

public sealed partial class MulticajaMovimientoCajaProcessor
{
    private readonly MulticajaIdempotencyRunner _idempotencyRunner;

    public MulticajaMovimientoCajaProcessor(
        PosCommerceDbContext db,
        ILogger<MulticajaMovimientoCajaProcessor> log,
        MulticajaIdempotencyRunner idempotencyRunner)
    {
        _db = db;
        _log = log;
        _idempotencyRunner = idempotencyRunner;
    }

    public Task<MulticajaMovimientoCajaResponse> RegistrarAsync(MulticajaMovimientoCajaRequest req,
        CancellationToken ct) =>
        _idempotencyRunner.ExecuteAsync(
            IdempotencyOperationType.CajaMovimiento,
            req,
            r => r.RequestId,
            r => r.CajaId,
            token => RegistrarCoreAsync(req, token),
            r => (r.Ok, r.Ok ? "caja_movimiento" : null, r.Ok ? req.CajaSesionId.ToString() : null),
            ct);
}
