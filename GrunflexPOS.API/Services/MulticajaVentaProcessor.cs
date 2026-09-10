using System.Data;
using GrunflexPOS.API.Commerce;
using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Inventory;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Services;

public sealed partial class MulticajaVentaProcessor
{
    private readonly PosCommerceDbContext _db;
    private readonly ILogger<MulticajaVentaProcessor> _log;
    private readonly CommerceActiveSessionValidator _sessionValidator;
    private readonly InventoryTransactionService _inventory;
    private readonly InventoryRetryPolicy _inventoryRetry;

    public async Task<MulticajaVentaCommitResponse> CommitCoreAsync(MulticajaVentaCommitRequest req, CancellationToken ct) =>
        await _inventoryRetry.ExecuteAsync(
            token => CommitCoreTransactionalAsync(req, token),
            isIdempotent: true,
            operationName: "multicaja.venta.commit",
            ct);

    private async Task<MulticajaVentaCommitResponse> CommitCoreTransactionalAsync(
        MulticajaVentaCommitRequest req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
            return Fail("MISSING_REQUEST_ID", "RequestId requerido para idempotencia.");

        if (req.Items == null || req.Items.Count == 0)
            return Fail("NO_ITEMS", "La venta no tiene líneas.");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var rid = req.RequestId.Trim();
            var dup = await IdempotencyLookupAsync(rid, ct);
            if (dup != null)
            {
                await tx.CommitAsync(ct);
                _log.LogInformation("multicaja.idempotent venta requestId={Rid} ticket={T}", rid, dup.Value.ticket);
                return new MulticajaVentaCommitResponse
                {
                    Ok = true,
                    NumeroTicket = dup.Value.ticket,
                    VentaId = dup.Value.ventaId
                };
            }

            var session = await _sessionValidator.ValidateCajaSessionFreshAsync(
                req.CajaId, req.CajaSesionId, req.UsuarioId, req.TerminalId, ct);
            if (!session.Ok)
            {
                await tx.RollbackAsync(ct);
                return Fail(session.Code, session.Message);
            }

            var sesion = await _db.CajaSesiones.FirstAsync(s => s.Id == req.CajaSesionId, ct);

            decimal totalCalculado = 0;
            foreach (var line in req.Items)
            {
                if (line.Cantidad <= 0)
                {
                    await tx.RollbackAsync(ct);
                    return Fail("CANTIDAD_INVALIDA", "Cantidad inválida en línea.");
                }

                totalCalculado += line.Precio * line.Cantidad;
            }

            if (!req.EsConsumoPersonal && totalCalculado <= 0)
            {
                await tx.RollbackAsync(ct);
                return Fail("TOTAL_INVALIDO", "Total de venta inválido.");
            }

            var resolved = new List<(MulticajaVentaLineaDto Line, CommerceProducto Product)>();
            foreach (var line in req.Items)
            {
                var p = await MulticajaProductoResolve.PorLineaAsync(_db, line, ct);
                if (p == null)
                {
                    await tx.RollbackAsync(ct);
                    return Fail("PRODUCTO_NO_ENCONTRADO", $"Producto no encontrado: {line.Producto}");
                }

                resolved.Add((line, p));
            }

            var ventaId = Guid.NewGuid();

            var invLines = new List<InventoryLineChange>();
            foreach (var (line, product) in resolved)
            {
                if (line.Componentes is { Count: > 0 })
                {
                    var partAfter = new Dictionary<int, int>();
                    var kitParts = new List<(int ProductId, int QtyPerKit)>();
                    foreach (var comp in line.Componentes)
                    {
                        if (comp.CantidadPorKit <= 0)
                        {
                            await tx.RollbackAsync(ct);
                            return Fail("CANTIDAD_INVALIDA", "Cantidad de componente de promoción inválida.");
                        }

                        var part = await MulticajaProductoResolve.PorComponenteAsync(_db, comp, ct);
                        if (part is null)
                        {
                            await tx.RollbackAsync(ct);
                            return Fail("PRODUCTO_NO_ENCONTRADO",
                                $"Componente de promoción no encontrado: {comp.Producto ?? comp.CodigoBarras}");
                        }

                        var need = comp.CantidadPorKit * line.Cantidad;
                        invLines.Add(new InventoryLineChange
                        {
                            ProductId = part.Id,
                            QuantityDelta = -need
                        });
                        var before = partAfter.TryGetValue(part.Id, out var projected)
                            ? projected
                            : part.Stock;
                        partAfter[part.Id] = before - need;
                        kitParts.Add((part.Id, comp.CantidadPorKit));
                    }

                    // Stock de la promoción = kits restantes tras descontar componentes.
                    decimal? kits = null;
                    foreach (var (partId, qtyPerKit) in kitParts)
                    {
                        if (!partAfter.TryGetValue(partId, out var after) || qtyPerKit <= 0)
                            continue;
                        var available = Math.Floor((decimal)after / qtyPerKit);
                        kits = kits is null ? available : Math.Min(kits.Value, available);
                    }

                    var targetKits = (int)Math.Max(0m, kits ?? 0m);
                    var promoDelta = targetKits - product.Stock;
                    if (promoDelta != 0)
                    {
                        invLines.Add(new InventoryLineChange
                        {
                            ProductId = product.Id,
                            QuantityDelta = promoDelta
                        });
                    }
                }
                else
                {
                    invLines.Add(new InventoryLineChange
                    {
                        ProductId = product.Id,
                        QuantityDelta = -line.Cantidad
                    });
                }
            }

            // Consumo personal también descuenta inventario (sin impacto en caja/total).
            var inv = await _inventory.ApplyStockChangesAsync(
                new InventoryStockChangeRequest
                {
                    Operation = MulticajaInventoryContextFactory.FromVenta(req),
                    Lines = invLines,
                    MovementType = InventoryMovementType.Sale,
                    ReferenceType = InventoryReferenceType.Venta,
                    ReferenceId = ventaId.ToString(),
                    AllowNegativeStock = false
                },
                ct);

            if (!inv.Ok)
            {
                await tx.RollbackAsync(ct);
                return Fail(inv.ErrorCode ?? "STOCK_INSUFICIENTE", inv.Error ?? "Stock insuficiente.");
            }

            var maxTicket = await _db.Ventas.MaxAsync(v => (int?)v.NumeroTicket, ct) ?? 0;
            var nextTicket = maxTicket + 1;
            var totalPersist = req.EsConsumoPersonal ? 0m : totalCalculado;
            var cajeroNombre = await _db.Usuarios.Where(u => u.Id == req.UsuarioId).Select(u => u.Username)
                .FirstAsync(ct);

            var venta = new CommerceVenta
            {
                Id = ventaId,
                NumeroTicket = nextTicket,
                Fecha = DateTime.UtcNow,
                Total = totalPersist,
                NumeroCaja = sesion.NumeroCaja,
                CajaId = req.CajaId,
                Cajero = cajeroNombre,
                Cliente = string.IsNullOrWhiteSpace(req.Cliente) ? "Público en general" : req.Cliente.Trim(),
                MetodoPago = string.IsNullOrWhiteSpace(req.MetodoPago) ? "Efectivo" : req.MetodoPago.Trim(),
                EstaAnulada = false,
                FechaAnulacion = null,
                UsuarioId = req.UsuarioId,
                CajaSesionId = req.CajaSesionId,
                EsConsumoPersonal = req.EsConsumoPersonal
            };

            _db.Ventas.Add(venta);

            foreach (var (line, _) in resolved)
            {
                _db.DetalleVentas.Add(new CommerceDetalleVenta
                {
                    Id = Guid.NewGuid(),
                    VentaId = ventaId,
                    CodigoBarras = line.CodigoBarras,
                    Producto = line.Producto,
                    Cantidad = line.Cantidad,
                    Precio = line.Precio
                });
            }

            if (!req.EsConsumoPersonal)
            {
                var cashImpact = MulticajaCashImpact.ForSale(
                    req.MetodoPago, totalPersist, req.MontoEfectivo);
                sesion.TotalVentas += cashImpact;
                if (cashImpact > 0)
                {
                    _db.MovimientosCaja.Add(new CommerceMovimientoCaja
                    {
                        Id = Guid.NewGuid(),
                        CajaSesionId = sesion.Id,
                        Fecha = DateTime.UtcNow,
                        Tipo = "VENTA",
                        Monto = cashImpact,
                        Descripcion = $"Venta Ticket #{nextTicket}"
                    });
                }
            }

            await _db.SaveChangesAsync(ct);

            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO "MulticajaVentaIdempotency" ("RequestId","VentaId","NumeroTicket","CreatedUtc")
                 VALUES ({rid},{ventaId},{nextTicket},{DateTime.UtcNow:O});
                 """,
                ct);

            await tx.CommitAsync(ct);

            _log.LogInformation(
                "multicaja.venta ok ticket={T} ventaId={V} caja={C} requestId={R} lines={N}",
                nextTicket, ventaId, req.CajaId, rid, req.Items.Count);

            return new MulticajaVentaCommitResponse
            {
                Ok = true,
                NumeroTicket = nextTicket,
                VentaId = ventaId
            };
        }
        catch (PosConcurrencyException pex)
        {
            try { await tx.RollbackAsync(ct); } catch { /* */ }
            _log.LogWarning(pex, "multicaja.venta concurrency requestId={Rid}", req.RequestId);
            return Fail(pex.ErrorCode, pex.UserMessage);
        }
        catch (Exception ex)
        {
            var pos = DatabaseErrorTranslator.TryTranslate(ex);
            if (pos != null)
            {
                try { await tx.RollbackAsync(ct); } catch { /* */ }
                _log.LogWarning(ex, "multicaja.venta db concurrency requestId={Rid}", req.RequestId);
                return Fail(pos.ErrorCode, pos.UserMessage);
            }

            try { await tx.RollbackAsync(ct); } catch { /* */ }
            _log.LogError(ex, "multicaja.venta error requestId={Rid}", req.RequestId);
            return Fail("ERROR_INTERNO", ex.Message);
        }
    }

    private async Task<(Guid ventaId, int ticket)?> IdempotencyLookupAsync(string requestId, CancellationToken ct)
    {
        var row = await _db.Database
            .SqlQuery<IdemRow>($"""
                                 SELECT "VentaId" AS VentaId, "NumeroTicket" AS NumeroTicket
                                 FROM "MulticajaVentaIdempotency"
                                 WHERE "RequestId" = {requestId}
                                 LIMIT 1
                                 """)
            .FirstOrDefaultAsync(ct);
        if (row == null) return null;
        return (row.VentaId, row.NumeroTicket);
    }

    private sealed class IdemRow
    {
        public Guid VentaId { get; set; }
        public int NumeroTicket { get; set; }
    }

    private static MulticajaVentaCommitResponse Fail(string code, string msg) =>
        new() { Ok = false, ErrorCode = code, Error = msg };
}
