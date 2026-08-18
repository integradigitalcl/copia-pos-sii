using System.Data;
using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GrunflexPOS.API.Services;

public sealed partial class MulticajaCierreCajaProcessor
{
    private readonly PosCommerceDbContext _db;
    private readonly ILogger<MulticajaCierreCajaProcessor> _log;

    public async Task<MulticajaCierreCajaResponse> CerrarCoreAsync(MulticajaCierreCajaRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
            return Fail("MISSING_REQUEST_ID", "RequestId requerido para idempotencia.");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var rid = req.RequestId.Trim();
            var idem = await IdempotencyLookupAsync(rid, ct);
            if (idem != null)
            {
                await tx.CommitAsync(ct);
                _log.LogInformation("multicaja.idempotent cierre requestId={R}", rid);
                return new MulticajaCierreCajaResponse
                {
                    Ok = true,
                    Esperado = idem.Value.esperado,
                    MontoContado = idem.Value.contado,
                    Diferencia = idem.Value.diferencia
                };
            }

            var usuarioOk = await _db.Usuarios.AnyAsync(u => u.Id == req.UsuarioCierreId, ct);
            if (!usuarioOk)
                return await RollbackAsync(tx, ct, "USUARIO_INVALIDO", "Usuario de cierre no existe.");

            var sesion = await _db.CajaSesiones.FirstOrDefaultAsync(s => s.Id == req.CajaSesionId, ct);
            if (sesion == null || sesion.CajaId != req.CajaId)
                return await RollbackAsync(tx, ct, "SESION_NO_ENCONTRADA", "Sesión no encontrada.");

            if (!sesion.Abierta)
            {
                _log.LogWarning("multicaja.cierre ya cerrada sesion={S}", req.CajaSesionId);
                return await RollbackAsync(tx, ct, "SESION_YA_CERRADA", "La sesión ya estaba cerrada.");
            }

            var esperado = sesion.MontoApertura + sesion.TotalVentas + sesion.TotalIngresos - sesion.TotalRetiros;
            var diferencia = req.MontoContado - esperado;

            sesion.UsuarioCierreId = req.UsuarioCierreId;
            sesion.FechaCierre = DateTime.UtcNow;
            sesion.MontoCierre = req.MontoContado;
            sesion.Diferencia = diferencia;
            sesion.Abierta = false;

            await _db.SaveChangesAsync(ct);

            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO "MulticajaCierreIdempotency"
                 ("RequestId","CajaSesionId","Esperado","MontoContado","Diferencia","CreatedUtc")
                 VALUES ({rid},{req.CajaSesionId},{esperado},{req.MontoContado},{diferencia},{DateTime.UtcNow:O});
                 """,
                ct);

            await tx.CommitAsync(ct);

            _log.LogInformation(
                "multicaja.cierre ok sesion={S} caja={C} esperado={E} contado={M} dif={D} requestId={R}",
                req.CajaSesionId, req.CajaId, esperado, req.MontoContado, diferencia, rid);

            return new MulticajaCierreCajaResponse
            {
                Ok = true,
                Esperado = esperado,
                MontoContado = req.MontoContado,
                Diferencia = diferencia
            };
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(ct); } catch { /* */ }
            _log.LogError(ex, "multicaja.cierre error requestId={Rid}", req.RequestId);
            return Fail("ERROR_INTERNO", ex.Message);
        }
    }

    private async Task<(decimal esperado, decimal contado, decimal diferencia)?> IdempotencyLookupAsync(
        string requestId, CancellationToken ct)
    {
        var row = await _db.Database
            .SqlQuery<IdemRow>($"""
                                  SELECT "Esperado" AS Esperado, "MontoContado" AS MontoContado,
                                         "Diferencia" AS Diferencia
                                  FROM "MulticajaCierreIdempotency"
                                  WHERE "RequestId" = {requestId}
                                  LIMIT 1
                                  """)
            .FirstOrDefaultAsync(ct);
        if (row == null) return null;
        return (row.Esperado, row.MontoContado, row.Diferencia);
    }

    private sealed class IdemRow
    {
        public decimal Esperado { get; set; }
        public decimal MontoContado { get; set; }
        public decimal Diferencia { get; set; }
    }

    private async Task<MulticajaCierreCajaResponse> RollbackAsync(IDbContextTransaction tx, CancellationToken ct,
        string code, string msg)
    {
        await tx.RollbackAsync(ct);
        _log.LogWarning("multicaja.cierre fail code={C} msg={M}", code, msg);
        return Fail(code, msg);
    }

    private static MulticajaCierreCajaResponse Fail(string code, string msg) =>
        new() { Ok = false, ErrorCode = code, Error = msg };
}
