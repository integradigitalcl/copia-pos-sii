using System.Data;
using GrunflexPOS.API.Commerce;
using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GrunflexPOS.API.Services;

public sealed partial class MulticajaDevolucionProcessor
{
    private readonly PosCommerceDbContext _db;
    private readonly ILogger<MulticajaDevolucionProcessor> _log;
    private readonly InventoryTransactionService _inventory;
    private readonly InventoryRetryPolicy _inventoryRetry;

    public async Task<MulticajaDevolucionLineaResponse> DevolverLineaCoreAsync(MulticajaDevolucionLineaRequest req,
        CancellationToken ct) =>
        await _inventoryRetry.ExecuteAsync(
            token => DevolverLineaTransactionalAsync(req, token),
            isIdempotent: true,
            operationName: "multicaja.devolucion",
            ct);

    private async Task<MulticajaDevolucionLineaResponse> DevolverLineaTransactionalAsync(
        MulticajaDevolucionLineaRequest req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
            return Fail("MISSING_REQUEST_ID", "RequestId requerido para idempotencia.");

        if (req.NumeroTicket <= 0 || req.Cantidad <= 0)
            return Fail("PARAMETRO_INVALIDO", "Ticket o cantidad inválidos.");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var rid = req.RequestId.Trim();
            var idem = await IdempotencyLookupAsync(rid, ct);
            if (idem != null)
            {
                await tx.CommitAsync(ct);
                _log.LogInformation("multicaja.idempotent devolucion requestId={R} ticket={T}", rid,
                    idem.Value.ticket);
                return new MulticajaDevolucionLineaResponse
                {
                    Ok = true,
                    NumeroTicket = idem.Value.ticket,
                    MontoDevuelto = idem.Value.monto,
                    NuevoTotalVenta = idem.Value.nuevoTotal
                };
            }

            var usuario = await _db.Usuarios.AsNoTracking().FirstOrDefaultAsync(u => u.Id == req.UsuarioId, ct);
            if (usuario == null)
                return await RollbackAsync(tx, ct, "USUARIO_INVALIDO", "Usuario no existe.");

            if (!MulticajaPermisosHelper.PuedeDevolverStock(usuario))
            {
                _log.LogWarning("multicaja.devolucion denegada usuario={U} rol={R}", req.UsuarioId, usuario.Rol);
                return await RollbackAsync(tx, ct, "SIN_PERMISO", "El usuario no puede registrar devoluciones.");
            }

            var venta = await _db.Ventas.FirstOrDefaultAsync(v => v.NumeroTicket == req.NumeroTicket, ct);
            if (venta == null)
                return await RollbackAsync(tx, ct, "VENTA_NO_ENCONTRADA", "No existe la venta.");

            if (venta.EstaAnulada)
                return await RollbackAsync(tx, ct, "VENTA_ANULADA", "La venta está anulada.");

            if (venta.CajaSesionId != req.CajaSesionId || venta.CajaId != req.CajaId)
                return await RollbackAsync(tx, ct, "SCOPE_INVALIDO", "La venta no pertenece a la sesión/caja indicada.");

            var sesion = await _db.CajaSesiones.FirstOrDefaultAsync(s => s.Id == req.CajaSesionId && s.Abierta, ct);
            if (sesion == null || sesion.CajaId != req.CajaId)
                return await RollbackAsync(tx, ct, "SESION_INVALIDA", "Sesión de caja cerrada o inválida.");

            var q = _db.DetalleVentas.Where(d =>
                d.VentaId == venta.Id &&
                d.Precio == req.Precio &&
                d.Producto == req.Producto);

            if (!string.IsNullOrWhiteSpace(req.CodigoBarras))
            {
                var cod = req.CodigoBarras.Trim();
                q = q.Where(d => d.CodigoBarras == cod);
            }

            var det = await q.OrderByDescending(d => d.Cantidad).FirstOrDefaultAsync(ct);
            if (det == null)
                return await RollbackAsync(tx, ct, "LINEA_NO_ENCONTRADA", "No se encontró la línea en el ticket.");

            if (det.Cantidad < req.Cantidad)
                return await RollbackAsync(tx, ct, "CANTIDAD_EXCESO", "La cantidad a devolver supera lo vendido.");

            var lineDto = new MulticajaVentaLineaDto
            {
                CodigoBarras = det.CodigoBarras,
                Producto = det.Producto,
                Cantidad = req.Cantidad,
                Precio = det.Precio
            };
            var p = await MulticajaProductoResolve.PorLineaAsync(_db, lineDto, ct);
            if (p == null)
                return await RollbackAsync(tx, ct, "PRODUCTO_NO_ENCONTRADO", "Producto no encontrado para restaurar stock.");

            var montoDevuelto = det.Precio * req.Cantidad;

            var inv = await _inventory.ApplyStockChangesAsync(
                new InventoryStockChangeRequest
                {
                    Operation = MulticajaInventoryContextFactory.FromDevolucion(req),
                    Lines =
                    [
                        new InventoryLineChange
                        {
                            ProductId = p.Id,
                            QuantityDelta = req.Cantidad,
                            UnitCostForInbound = p.Costo > 0 ? p.Costo : null
                        }
                    ],
                    MovementType = InventoryMovementType.SaleReturn,
                    ReferenceType = InventoryReferenceType.Devolucion,
                    ReferenceId = venta.Id.ToString(),
                    AllowNegativeStock = false
                },
                ct);

            if (!inv.Ok)
                return await RollbackAsync(tx, ct, inv.ErrorCode ?? "STOCK_ERROR", inv.Error ?? "Error de inventario.");

            det.Cantidad -= req.Cantidad;
            if (det.Cantidad <= 0)
                _db.DetalleVentas.Remove(det);

            venta.Total = Math.Max(0, venta.Total - montoDevuelto);

            if (!venta.EsConsumoPersonal && montoDevuelto > 0)
            {
                var cashImpact = MulticajaCashImpact.ForRefund(
                    venta.MetodoPago, venta.Total + montoDevuelto, montoDevuelto, montoEfectivo: null);
                if (cashImpact > 0)
                {
                    sesion.TotalVentas = Math.Max(0, sesion.TotalVentas - cashImpact);
                    _db.MovimientosCaja.Add(new CommerceMovimientoCaja
                    {
                        Id = Guid.NewGuid(),
                        CajaSesionId = sesion.Id,
                        Fecha = DateTime.UtcNow,
                        Tipo = "DEVOLUCION",
                        Monto = -cashImpact,
                        Descripcion = $"Devolución Ticket #{req.NumeroTicket} ({req.Producto})"
                    });
                }
            }

            await _db.SaveChangesAsync(ct);

            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO "MulticajaDevolucionIdempotency"
                 ("RequestId","NumeroTicket","MontoDevuelto","NuevoTotalVenta","CreatedUtc")
                 VALUES ({rid},{req.NumeroTicket},{montoDevuelto},{venta.Total},{DateTime.UtcNow:O});
                 """,
                ct);

            await tx.CommitAsync(ct);

            _log.LogInformation(
                "multicaja.devolucion ok ticket={T} requestId={R} monto={M} nuevoTotal={Nt}",
                req.NumeroTicket, rid, montoDevuelto, venta.Total);

            return new MulticajaDevolucionLineaResponse
            {
                Ok = true,
                NumeroTicket = req.NumeroTicket,
                MontoDevuelto = montoDevuelto,
                NuevoTotalVenta = venta.Total
            };
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
            _log.LogError(ex, "multicaja.devolucion error requestId={Rid}", req.RequestId);
            return Fail("ERROR_INTERNO", ex.Message);
        }
    }

    private async Task<(int ticket, decimal monto, decimal nuevoTotal)?> IdempotencyLookupAsync(string requestId,
        CancellationToken ct)
    {
        var row = await _db.Database
            .SqlQuery<IdemRow>($"""
                                  SELECT "NumeroTicket" AS NumeroTicket, "MontoDevuelto" AS MontoDevuelto,
                                         "NuevoTotalVenta" AS NuevoTotalVenta
                                  FROM "MulticajaDevolucionIdempotency"
                                  WHERE "RequestId" = {requestId}
                                  LIMIT 1
                                  """)
            .FirstOrDefaultAsync(ct);
        if (row == null) return null;
        return (row.NumeroTicket, row.MontoDevuelto, row.NuevoTotalVenta);
    }

    private sealed class IdemRow
    {
        public int NumeroTicket { get; set; }
        public decimal MontoDevuelto { get; set; }
        public decimal NuevoTotalVenta { get; set; }
    }

    private async Task<MulticajaDevolucionLineaResponse> RollbackAsync(IDbContextTransaction tx, CancellationToken ct,
        string code, string msg)
    {
        await tx.RollbackAsync(ct);
        _log.LogWarning("multicaja.devolucion fail code={C} msg={M}", code, msg);
        return Fail(code, msg);
    }

    private static MulticajaDevolucionLineaResponse Fail(string code, string msg) =>
        new() { Ok = false, ErrorCode = code, Error = msg };
}
