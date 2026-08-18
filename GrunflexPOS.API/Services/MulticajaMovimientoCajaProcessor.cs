using System.Data;
using GrunflexPOS.API.Commerce;
using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GrunflexPOS.API.Services;

public sealed partial class MulticajaMovimientoCajaProcessor
{
    private const int MaxDescripcion = 500;
    private readonly PosCommerceDbContext _db;
    private readonly ILogger<MulticajaMovimientoCajaProcessor> _log;

    public async Task<MulticajaMovimientoCajaResponse> RegistrarCoreAsync(MulticajaMovimientoCajaRequest req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
            return Fail("MISSING_REQUEST_ID", "RequestId requerido para idempotencia.");

        var tipoNorm = (req.Tipo ?? "").Trim().ToUpperInvariant();
        if (tipoNorm is not ("INGRESO" or "RETIRO"))
            return Fail("TIPO_INVALIDO", "Tipo debe ser INGRESO o RETIRO.");

        if (req.Monto <= 0)
            return Fail("MONTO_INVALIDO", "El monto debe ser mayor a cero.");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var rid = req.RequestId.Trim();
            var idem = await IdempotencyLookupAsync(rid, ct);
            if (idem != null)
            {
                await tx.CommitAsync(ct);
                _log.LogInformation(
                    "multicaja.idempotent movimiento_caja {Evt} requestId={R} sesion={S}",
                    idem.Value.Tipo == "INGRESO" ? "ingreso" : "retiro",
                    rid,
                    req.CajaSesionId);
                return new MulticajaMovimientoCajaResponse
                {
                    Ok = true,
                    Tipo = idem.Value.Tipo,
                    Monto = idem.Value.Monto,
                    TotalIngresos = idem.Value.TotalIngresos,
                    TotalRetiros = idem.Value.TotalRetiros,
                    TotalVentas = idem.Value.TotalVentas
                };
            }

            var usuarioOk = await _db.Usuarios.AnyAsync(u => u.Id == req.UsuarioId, ct);
            if (!usuarioOk)
                return await RollbackAsync(tx, ct, "USUARIO_INVALIDO", "Usuario no existe.");

            var sesion = await _db.CajaSesiones.FirstOrDefaultAsync(s => s.Id == req.CajaSesionId, ct);
            if (sesion == null || sesion.CajaId != req.CajaId)
                return await RollbackAsync(tx, ct, "SESION_NO_ENCONTRADA", "Sesión no encontrada.");

            if (!sesion.Abierta)
                return await RollbackAsync(tx, ct, "SESION_CERRADA", "No se pueden registrar movimientos en una caja cerrada.");

            var desc = string.IsNullOrWhiteSpace(req.Descripcion) ? string.Empty : req.Descripcion.Trim();
            if (desc.Length > MaxDescripcion)
                desc = desc[..MaxDescripcion];

            if (tipoNorm == "INGRESO")
                sesion.TotalIngresos += req.Monto;
            else
                sesion.TotalRetiros += req.Monto;

            _db.MovimientosCaja.Add(new CommerceMovimientoCaja
            {
                Id = Guid.NewGuid(),
                CajaSesionId = sesion.Id,
                Fecha = DateTime.UtcNow,
                Tipo = tipoNorm,
                Monto = req.Monto,
                Descripcion = desc
            });

            await _db.SaveChangesAsync(ct);

            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO "MulticajaMovimientoCajaIdempotency"
                 ("RequestId","CajaSesionId","Tipo","Monto","TotalIngresos","TotalRetiros","TotalVentas","CreatedUtc")
                 VALUES ({rid},{req.CajaSesionId},{tipoNorm},{req.Monto},{sesion.TotalIngresos},{sesion.TotalRetiros},{sesion.TotalVentas},{DateTime.UtcNow:O});
                 """,
                ct);

            await tx.CommitAsync(ct);

            var logKey = tipoNorm == "INGRESO" ? "multicaja.ingreso" : "multicaja.retiro";
            _log.LogInformation(
                "{LogKey} ok sesion={S} caja={C} monto={M} usuario={U} requestId={R} totIng={Ti} totRet={Tr} totVta={Tv}",
                logKey,
                req.CajaSesionId,
                req.CajaId,
                req.Monto,
                req.UsuarioId,
                rid,
                sesion.TotalIngresos,
                sesion.TotalRetiros,
                sesion.TotalVentas);

            return new MulticajaMovimientoCajaResponse
            {
                Ok = true,
                Tipo = tipoNorm,
                Monto = req.Monto,
                TotalIngresos = sesion.TotalIngresos,
                TotalRetiros = sesion.TotalRetiros,
                TotalVentas = sesion.TotalVentas
            };
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(ct); } catch { /* */ }
            _log.LogError(ex, "multicaja.movimiento error requestId={Rid}", req.RequestId);
            return Fail("ERROR_INTERNO", ex.Message);
        }
    }

    private async Task<(string Tipo, decimal Monto, decimal TotalIngresos, decimal TotalRetiros, decimal TotalVentas)?>
        IdempotencyLookupAsync(string requestId, CancellationToken ct)
    {
        var row = await _db.Database
            .SqlQuery<IdemRow>($"""
                                  SELECT "Tipo" AS Tipo, "Monto" AS Monto,
                                         "TotalIngresos" AS TotalIngresos, "TotalRetiros" AS TotalRetiros,
                                         "TotalVentas" AS TotalVentas
                                  FROM "MulticajaMovimientoCajaIdempotency"
                                  WHERE "RequestId" = {requestId}
                                  LIMIT 1
                                  """)
            .FirstOrDefaultAsync(ct);
        if (row == null) return null;
        return (row.Tipo, row.Monto, row.TotalIngresos, row.TotalRetiros, row.TotalVentas);
    }

    private sealed class IdemRow
    {
        public string Tipo { get; set; } = "";
        public decimal Monto { get; set; }
        public decimal TotalIngresos { get; set; }
        public decimal TotalRetiros { get; set; }
        public decimal TotalVentas { get; set; }
    }

    private async Task<MulticajaMovimientoCajaResponse> RollbackAsync(IDbContextTransaction tx, CancellationToken ct,
        string code, string msg)
    {
        await tx.RollbackAsync(ct);
        _log.LogWarning("multicaja.movimiento fail code={C} msg={M}", code, msg);
        return Fail(code, msg);
    }

    private static MulticajaMovimientoCajaResponse Fail(string code, string msg) =>
        new() { Ok = false, ErrorCode = code, Error = msg };
}
