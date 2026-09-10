using System.Data;
using GrunflexPOS.API.Commerce;
using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GrunflexPOS.API.Services;

public sealed partial class MulticajaAnulacionProcessor
{
    private readonly PosCommerceDbContext _db;
    private readonly ILogger<MulticajaAnulacionProcessor> _log;
    private readonly InventoryTransactionService _inventory;
    private readonly InventoryRetryPolicy _inventoryRetry;

    public async Task<MulticajaAnularVentaResponse> AnularCoreAsync(MulticajaAnularVentaRequest req, CancellationToken ct) =>
        await _inventoryRetry.ExecuteAsync(
            token => AnularCoreTransactionalAsync(req, token),
            isIdempotent: true,
            operationName: "multicaja.anulacion",
            ct);

    private async Task<MulticajaAnularVentaResponse> AnularCoreTransactionalAsync(
        MulticajaAnularVentaRequest req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
            return Fail("MISSING_REQUEST_ID", "RequestId requerido para idempotencia.");

        if (req.NumeroTicket <= 0)
            return Fail("TICKET_INVALIDO", "Número de ticket inválido.");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var rid = req.RequestId.Trim();
            var idem = await IdempotencyLookupAsync(rid, ct);
            if (idem != null)
            {
                await tx.CommitAsync(ct);
                _log.LogInformation("multicaja.idempotent anulacion requestId={R} ticket={T}", rid, idem.Value);
                return new MulticajaAnularVentaResponse { Ok = true, NumeroTicket = idem.Value };
            }

            var usuario = await _db.Usuarios.AsNoTracking().FirstOrDefaultAsync(u => u.Id == req.UsuarioId, ct);
            if (usuario == null)
                return await RollbackAsync(tx, ct, "USUARIO_INVALIDO", "Usuario no existe.");

            if (!MulticajaPermisosHelper.PuedeAnularTickets(usuario))
            {
                _log.LogWarning("multicaja.anulacion denegada usuario={U} rol={R}", req.UsuarioId, usuario.Rol);
                return await RollbackAsync(tx, ct, "SIN_PERMISO", "El usuario no puede anular tickets.");
            }

            var venta = await _db.Ventas.FirstOrDefaultAsync(v => v.NumeroTicket == req.NumeroTicket, ct);
            if (venta == null)
                return await RollbackAsync(tx, ct, "VENTA_NO_ENCONTRADA", "No existe la venta.");

            if (venta.EstaAnulada)
                return await RollbackAsync(tx, ct, "YA_ANULADA", "La venta ya está anulada.");

            if (venta.CajaSesionId != req.CajaSesionId || venta.CajaId != req.CajaId)
            {
                _log.LogWarning(
                    "multicaja.anulacion sesion/caja no coincide ticket={T} ventaSesion={Vs} reqSesion={Rs}",
                    req.NumeroTicket, venta.CajaSesionId, req.CajaSesionId);
                return await RollbackAsync(tx, ct, "SCOPE_INVALIDO",
                    "La venta no pertenece a la sesión/caja indicada (solo turno actual).");
            }

            var sesion = await _db.CajaSesiones.FirstOrDefaultAsync(s => s.Id == req.CajaSesionId && s.Abierta, ct);
            if (sesion == null || sesion.CajaId != req.CajaId)
                return await RollbackAsync(tx, ct, "SESION_INVALIDA", "Sesión de caja cerrada o inválida.");

            var lineas = await _db.DetalleVentas.Where(d => d.VentaId == venta.Id).ToListAsync(ct);
            var invLines = new List<InventoryLineChange>();
            foreach (var ln in lineas)
            {
                var lineDto = new MulticajaVentaLineaDto
                {
                    CodigoBarras = ln.CodigoBarras,
                    Producto = ln.Producto,
                    Cantidad = ln.Cantidad,
                    Precio = ln.Precio
                };
                var p = await MulticajaProductoResolve.PorLineaAsync(_db, lineDto, ct);
                if (p != null)
                {
                    invLines.Add(new InventoryLineChange
                    {
                        ProductId = p.Id,
                        QuantityDelta = ln.Cantidad,
                        UnitCostForInbound = p.Costo > 0 ? p.Costo : null
                    });
                }
            }

            if (invLines.Count > 0)
            {
                var inv = await _inventory.ApplyStockChangesAsync(
                    new InventoryStockChangeRequest
                    {
                        Operation = MulticajaInventoryContextFactory.FromAnulacion(req),
                        Lines = invLines,
                        MovementType = InventoryMovementType.VoidSale,
                        ReferenceType = InventoryReferenceType.Anulacion,
                        ReferenceId = venta.Id.ToString(),
                        AllowNegativeStock = false
                    },
                    ct);

                if (!inv.Ok)
                    return await RollbackAsync(tx, ct, inv.ErrorCode ?? "STOCK_ERROR", inv.Error ?? "Error de inventario.");
            }

            venta.EstaAnulada = true;
            venta.FechaAnulacion = DateTime.UtcNow;

            if (!venta.EsConsumoPersonal && venta.Total > 0)
            {
                var cashImpact = MulticajaCashImpact.ForSale(venta.MetodoPago, venta.Total, montoEfectivo: null);
                if (cashImpact > 0)
                {
                    sesion.TotalVentas = Math.Max(0, sesion.TotalVentas - cashImpact);
                    _db.MovimientosCaja.Add(new CommerceMovimientoCaja
                    {
                        Id = Guid.NewGuid(),
                        CajaSesionId = sesion.Id,
                        Fecha = DateTime.UtcNow,
                        Tipo = "ANULACION",
                        Monto = -cashImpact,
                        Descripcion = $"Anulación Ticket #{req.NumeroTicket}"
                    });
                }
            }

            await _db.SaveChangesAsync(ct);

            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO "MulticajaAnulacionIdempotency" ("RequestId","NumeroTicket","CreatedUtc")
                 VALUES ({rid},{req.NumeroTicket},{DateTime.UtcNow:O});
                 """,
                ct);

            await tx.CommitAsync(ct);

            _log.LogInformation(
                "multicaja.anulacion ok ticket={T} requestId={R} usuario={U} lineas={N}",
                req.NumeroTicket, rid, req.UsuarioId, lineas.Count);

            return new MulticajaAnularVentaResponse { Ok = true, NumeroTicket = req.NumeroTicket };
        }
        catch (PosConcurrencyException pex)
        {
            try { await tx.RollbackAsync(ct); } catch { /* */ }
            return Fail(pex.ErrorCode, pex.UserMessage);
        }
        catch (Exception ex)
        {
            var pos = DatabaseErrorTranslator.TryTranslate(ex);
            if (pos != null)
            {
                try { await tx.RollbackAsync(ct); } catch { /* */ }
                return Fail(pos.ErrorCode, pos.UserMessage);
            }

            try { await tx.RollbackAsync(ct); } catch { /* */ }
            _log.LogError(ex, "multicaja.anulacion error requestId={Rid}", req.RequestId);
            return Fail("ERROR_INTERNO", ex.Message);
        }
    }

    private async Task<int?> IdempotencyLookupAsync(string requestId, CancellationToken ct)
    {
        var row = await _db.Database
            .SqlQuery<IdemRow>($"""
                                  SELECT "NumeroTicket" AS NumeroTicket
                                  FROM "MulticajaAnulacionIdempotency"
                                  WHERE "RequestId" = {requestId}
                                  LIMIT 1
                                  """)
            .FirstOrDefaultAsync(ct);
        return row?.NumeroTicket;
    }

    private sealed class IdemRow
    {
        public int NumeroTicket { get; set; }
    }

    private async Task<MulticajaAnularVentaResponse> RollbackAsync(IDbContextTransaction tx, CancellationToken ct,
        string code, string msg)
    {
        await tx.RollbackAsync(ct);
        _log.LogWarning("multicaja.anulacion fail code={C} msg={M}", code, msg);
        return Fail(code, msg);
    }

    private static MulticajaAnularVentaResponse Fail(string code, string msg) =>
        new() { Ok = false, ErrorCode = code, Error = msg };
}
