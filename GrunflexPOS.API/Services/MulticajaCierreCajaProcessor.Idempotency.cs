using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Idempotency;

namespace GrunflexPOS.API.Services;

public sealed partial class MulticajaCierreCajaProcessor
{
    private readonly MulticajaIdempotencyRunner _idempotencyRunner;

    public MulticajaCierreCajaProcessor(
        PosCommerceDbContext db,
        ILogger<MulticajaCierreCajaProcessor> log,
        MulticajaIdempotencyRunner idempotencyRunner)
    {
        _db = db;
        _log = log;
        _idempotencyRunner = idempotencyRunner;
    }

    public Task<MulticajaCierreCajaResponse> CerrarAsync(MulticajaCierreCajaRequest req, CancellationToken ct) =>
        _idempotencyRunner.ExecuteAsync(
            IdempotencyOperationType.CajaCierre,
            req,
            r => r.RequestId,
            r => r.CajaId,
            token => CerrarCoreAsync(req, token),
            r => (r.Ok, r.Ok ? "caja_sesion" : null, r.Ok ? req.CajaSesionId.ToString() : null),
            ct);
}
