using System.Data;
using GrunflexPOS.API.Commerce;
using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GrunflexPOS.API.Services;

public sealed partial class MulticajaInventarioProcessor
{
    private readonly PosCommerceDbContext _db;
    private readonly ILogger<MulticajaInventarioProcessor> _log;
    private readonly InventoryTransactionService _inventory;
    private readonly InventoryRetryPolicy _inventoryRetry;

    public async Task<MulticajaInventarioAjusteResponse> AjustarCoreAsync(
        MulticajaInventarioAjusteRequest req,
        CancellationToken ct) =>
        await _inventoryRetry.ExecuteAsync(
            token => AjustarCoreTransactionalAsync(req, token),
            isIdempotent: true,
            operationName: "multicaja.inventario.ajuste",
            ct);

    private async Task<MulticajaInventarioAjusteResponse> AjustarCoreTransactionalAsync(
        MulticajaInventarioAjusteRequest req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RequestId))
            return Fail("MISSING_REQUEST_ID", "RequestId requerido para idempotencia.");

        if (req.ProductoId <= 0)
            return Fail("PRODUCTO_INVALIDO", "ProductoId inválido.");

        if (req.CantidadDelta == 0)
            return Fail("CANTIDAD_INVALIDA", "La cantidad delta no puede ser cero.");

        if (req.CajaId == Guid.Empty)
            return Fail("CAJA_INVALIDA", "CajaId requerido.");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var usuario = await _db.Usuarios.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == req.UsuarioId, ct);
            if (usuario == null)
                return await RollbackAsync(tx, ct, "USUARIO_INVALIDO", "Usuario no existe.");

            if (!MulticajaPermisosHelper.PuedeAjustarInventario(usuario))
            {
                _log.LogWarning("multicaja.inventario.ajuste denegado usuario={U} rol={R}", req.UsuarioId, usuario.Rol);
                return await RollbackAsync(tx, ct, "SIN_PERMISO", "El usuario no puede ajustar inventario.");
            }

            var producto = await _db.Productos.FirstOrDefaultAsync(p => p.Id == req.ProductoId, ct);
            if (producto == null)
                return await RollbackAsync(tx, ct, "PRODUCTO_NO_ENCONTRADO", "Producto no encontrado.");

            var stockAnterior = producto.Stock;

            var inv = await _inventory.ApplyStockChangesAsync(
                new InventoryStockChangeRequest
                {
                    Operation = MulticajaInventoryContextFactory.FromInventarioAjuste(req),
                    Lines =
                    [
                        new InventoryLineChange
                        {
                            ProductId = req.ProductoId,
                            QuantityDelta = req.CantidadDelta,
                            UnitCostForInbound = req.CantidadDelta > 0 && producto.Costo > 0
                                ? producto.Costo
                                : null
                        }
                    ],
                    MovementType = InventoryMovementType.Adjustment,
                    ReferenceType = InventoryReferenceType.Ajuste,
                    ReferenceId = req.RequestId.Trim(),
                    AllowNegativeStock = false
                },
                ct);

            if (!inv.Ok)
                return await RollbackAsync(tx, ct, inv.ErrorCode ?? "STOCK_ERROR", inv.Error ?? "Error de inventario.");

            await _db.Entry(producto).ReloadAsync(ct);
            var stockNuevo = producto.Stock;

            await tx.CommitAsync(ct);

            _log.LogInformation(
                "multicaja.inventario.ajuste ok producto={P} delta={D} stock {Ant}→{Nuevo} requestId={R}",
                req.ProductoId, req.CantidadDelta, stockAnterior, stockNuevo, req.RequestId);

            return new MulticajaInventarioAjusteResponse
            {
                Ok = true,
                ProductoId = req.ProductoId,
                StockAnterior = stockAnterior,
                StockNuevo = stockNuevo
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
            _log.LogError(ex, "multicaja.inventario.ajuste error requestId={Rid}", req.RequestId);
            return Fail("ERROR_INTERNO", ex.Message);
        }
    }

    private async Task<MulticajaInventarioAjusteResponse> RollbackAsync(
        IDbContextTransaction tx,
        CancellationToken ct,
        string code,
        string msg)
    {
        await tx.RollbackAsync(ct);
        _log.LogWarning("multicaja.inventario.ajuste fail code={C} msg={M}", code, msg);
        return Fail(code, msg);
    }

    private static MulticajaInventarioAjusteResponse Fail(string code, string msg) =>
        new() { Ok = false, ErrorCode = code, Error = msg };
}
