using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using PosEdge.Domain;
using PosEdge.Infrastructure;

namespace PosEdge.Application.Sales;

public sealed record SaleCommitCommand(
    Guid TenantId,
    Guid BranchId,
    Guid TerminalId,
    Guid CashSessionId,
    string RequestId,
    string Currency,
    IReadOnlyList<SaleCommitLine> Lines,
    IReadOnlyList<SaleCommitPayment> Payments,
    decimal DiscountTotal,
    Guid? LeaseId) : IRequest<SaleCommitResult>;

public sealed record SaleCommitLine(
    Guid ProductId,
    string Sku,
    string Name,
    decimal Qty,
    decimal UnitPrice,
    decimal TaxRate);

public sealed record SaleCommitPayment(
    string Method,
    decimal Amount,
    string Currency,
    object? Meta);

public sealed record SaleCommitResult(
    bool Ok,
    string Code,
    string? Message,
    Guid? SaleId,
    long? ServerTicket,
    long? EventSeq);

public sealed class SaleCommitHandler : IRequestHandler<SaleCommitCommand, SaleCommitResult>
{
    private readonly PosEdgeDbContext _db;

    public SaleCommitHandler(PosEdgeDbContext db) => _db = db;

    public async Task<SaleCommitResult> Handle(SaleCommitCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.RequestId))
            return new(false, "VALIDATION_ERROR", "requestId requerido", null, null, null);
        if (cmd.Lines.Count == 0)
            return new(false, "VALIDATION_ERROR", "lines vacío", null, null, null);
        if (cmd.Payments.Count == 0)
            return new(false, "VALIDATION_ERROR", "payments vacío", null, null, null);

        // If leaseId is provided, enforce LEASES_ONLY rules:
        // - Lease must be active and not expired
        // - For each product in the sale, remaining (allocated - used) must cover qty
        // - Consume used quantities in the same DB transaction.

        // Idempotencia rápida: si existe la venta, devolver resultado sin repetir side effects.
        var existing = await _db.Sales.AsNoTracking()
            .Where(s => s.TenantId == cmd.TenantId && s.BranchId == cmd.BranchId && s.RequestId == cmd.RequestId)
            .Select(s => new { s.SaleId, s.ServerTicket })
            .FirstOrDefaultAsync(ct);
        if (existing != null)
        {
            // Buscar el event seq asociado (si existe) para Sync.
            var ev = await _db.EventLog.AsNoTracking()
                .Where(e => e.TenantId == cmd.TenantId && e.BranchId == cmd.BranchId && e.RequestId == cmd.RequestId)
                .OrderByDescending(e => e.Seq)
                .Select(e => e.Seq)
                .FirstOrDefaultAsync(ct);
            return new(true, "OK", null, existing.SaleId, existing.ServerTicket, ev == 0 ? null : ev);
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
                // Exactly-once hardening: prevent concurrent commits for same requestId from racing and
                // throwing unique constraint exceptions (which also create identity seq holes).
                // This lock is transaction-scoped; duplicates will wait and then observe the committed row.
                await _db.Database.ExecuteSqlInterpolatedAsync($"""
                    SELECT pg_advisory_xact_lock(hashtext({cmd.RequestId}));
                    """, ct);

                // Re-check idempotency after acquiring the lock (covers concurrent duplicates).
                var existingLocked = await _db.Sales.AsNoTracking()
                    .Where(s => s.TenantId == cmd.TenantId && s.BranchId == cmd.BranchId && s.RequestId == cmd.RequestId)
                    .Select(s => new { s.SaleId, s.ServerTicket })
                    .FirstOrDefaultAsync(ct);
                if (existingLocked != null)
                {
                    var evLocked = await _db.EventLog.AsNoTracking()
                        .Where(e => e.TenantId == cmd.TenantId && e.BranchId == cmd.BranchId && e.RequestId == cmd.RequestId)
                        .OrderByDescending(e => e.Seq)
                        .Select(e => e.Seq)
                        .FirstOrDefaultAsync(ct);
                    await tx.CommitAsync(ct);
                    return new(true, "OK", null, existingLocked.SaleId, existingLocked.ServerTicket, evLocked == 0 ? null : evLocked);
                }

                // 1) Validar cash session abierta (mínimo para MVP).
                var cash = await _db.CashSessions.AsNoTracking()
                    .Where(c => c.CashSessionId == cmd.CashSessionId && c.TenantId == cmd.TenantId && c.BranchId == cmd.BranchId)
                    .Select(c => new { c.Status })
                    .FirstOrDefaultAsync(ct);
                if (cash == null || !string.Equals(cash.Status, "open", StringComparison.OrdinalIgnoreCase))
                    return new SaleCommitResult(false, "SESSION_NOT_OPEN", "cashSession no abierta", null, null, null);

                // 2) Lock rows determinístico para evitar deadlocks.
                var productIds = cmd.Lines.Select(l => l.ProductId).Distinct().OrderBy(x => x).ToArray();

                InventoryLease? lease = null;
                if (cmd.LeaseId.HasValue)
                {
                    lease = await _db.InventoryLeases
                        .Include(l => l.Lines)
                        .SingleOrDefaultAsync(l => l.LeaseId == cmd.LeaseId.Value
                                                   && l.TenantId == cmd.TenantId
                                                   && l.BranchId == cmd.BranchId
                                                   && l.TerminalId == cmd.TerminalId, ct);
                    if (lease == null)
                        return new(false, "LEASE_INVALID", "lease no existe", null, null, null);
                    if (!string.Equals(lease.Status, "active", StringComparison.OrdinalIgnoreCase))
                        return new(false, lease.Status == "revoked" ? "LEASE_REVOKED" : "LEASE_INVALID", $"lease status={lease.Status}", null, null, null);
                    if (lease.ExpiresAt <= DateTimeOffset.UtcNow)
                    {
                        lease.Status = "expired";
                        await _db.SaveChangesAsync(ct);
                        await tx.RollbackAsync(ct);
                        return new(false, "LEASE_EXPIRED", "lease expirado", null, null, null);
                    }
                }

                await _db.Database.ExecuteSqlInterpolatedAsync($@"
                    SELECT 1
                    FROM inventory
                    WHERE tenant_id = {cmd.TenantId} AND branch_id = {cmd.BranchId} AND product_id = ANY({productIds})
                    ORDER BY product_id
                    FOR UPDATE", ct);

                // 3) Decrement stock via conditional update per product (aggregate quantities).
                var qtyByProduct = cmd.Lines
                    .GroupBy(l => l.ProductId)
                    .ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));

                if (lease != null)
                {
                    foreach (var (pid, qty) in qtyByProduct)
                    {
                        var line = lease.Lines.SingleOrDefault(x => x.ProductId == pid);
                        if (line == null)
                        {
                            await tx.RollbackAsync(ct);
                            return new(false, "LEASE_EXHAUSTED", $"lease sin producto {pid}", null, null, null);
                        }
                        var remaining = line.QtyAllocated - line.QtyUsed;
                        if (remaining < qty)
                        {
                            await tx.RollbackAsync(ct);
                            return new(false, "LEASE_EXHAUSTED", $"lease insuficiente {pid}", null, null, null);
                        }
                    }
                    // consume
                    foreach (var (pid, qty) in qtyByProduct)
                    {
                        var ll = lease.Lines.Single(x => x.ProductId == pid);
                        ll.QtyUsed += qty;
                    }

                    // Release reserved immediately for consumed quantity so reserved doesn't "stick" until expiry.
                    // The reserved stock represents allocated-but-unused lease units.
                    foreach (var (pid, qty) in qtyByProduct)
                    {
                        await _db.Database.ExecuteSqlInterpolatedAsync($@"
                        UPDATE inventory
                           SET reserved = GREATEST(0, reserved - {qty}),
                               updated_at = now()
                         WHERE tenant_id = {cmd.TenantId}
                           AND branch_id = {cmd.BranchId}
                           AND product_id = {pid};", ct);
                    }
                }

                foreach (var (pid, qty) in qtyByProduct)
                {
                    if (qty <= 0)
                        return new SaleCommitResult(false, "VALIDATION_ERROR", $"qty inválida {pid}", null, null, null);

                    var rows = await _db.Database.ExecuteSqlInterpolatedAsync($@"
                        UPDATE inventory
                           SET on_hand = on_hand - {qty},
                               updated_at = now()
                         WHERE tenant_id = {cmd.TenantId}
                           AND branch_id = {cmd.BranchId}
                           AND product_id = {pid}
                           AND on_hand >= {qty}", ct);

                    if (rows == 0)
                    {
                        await tx.RollbackAsync(ct);
                        return new SaleCommitResult(false, "INSUFFICIENT_STOCK", $"Stock insuficiente para {pid}", null, null, null);
                    }
                }

                // 4) Ticket: atomically increment counter row.
                var counter = await _db.Counters.SingleOrDefaultAsync(
                    c => c.TenantId == cmd.TenantId && c.BranchId == cmd.BranchId && c.Key == "ticket", ct);
                if (counter == null)
                {
                    counter = new Counter
                    {
                        TenantId = cmd.TenantId,
                        BranchId = cmd.BranchId,
                        Key = "ticket",
                        Value = 0,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    _db.Counters.Add(counter);
                    await _db.SaveChangesAsync(ct);
                }

                await _db.Database.ExecuteSqlInterpolatedAsync($@"
                    SELECT 1 FROM counters
                     WHERE tenant_id = {cmd.TenantId} AND branch_id = {cmd.BranchId} AND key = {"ticket"}
                     FOR UPDATE", ct);
                counter.Value += 1;
                counter.UpdatedAt = DateTimeOffset.UtcNow;

                // 5) Totals
                decimal subtotal = cmd.Lines.Sum(l => l.UnitPrice * l.Qty);
                subtotal = Math.Max(0, subtotal - cmd.DiscountTotal);
                decimal tax = cmd.Lines.Sum(l => (l.UnitPrice * l.Qty) * l.TaxRate);
                decimal total = subtotal + tax;
                if (total < 0) total = 0;

                var sale = new Sale
                {
                    SaleId = Guid.NewGuid(),
                    TenantId = cmd.TenantId,
                    BranchId = cmd.BranchId,
                    TerminalId = cmd.TerminalId,
                    CashSessionId = cmd.CashSessionId,
                    RequestId = cmd.RequestId,
                    ServerTicket = counter.Value,
                    Currency = cmd.Currency,
                    Subtotal = subtotal,
                    Tax = tax,
                    Total = total,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                foreach (var l in cmd.Lines)
                {
                    var lineSubtotal = l.UnitPrice * l.Qty;
                    var lineTax = lineSubtotal * l.TaxRate;
                    sale.Lines.Add(new SaleLine
                    {
                        SaleLineId = Guid.NewGuid(),
                        SaleId = sale.SaleId,
                        ProductId = l.ProductId,
                        Sku = l.Sku,
                        Name = l.Name,
                        Qty = l.Qty,
                        UnitPrice = l.UnitPrice,
                        TaxRate = l.TaxRate,
                        LineSubtotal = lineSubtotal,
                        LineTax = lineTax,
                        LineTotal = lineSubtotal + lineTax
                    });
                }
                foreach (var p in cmd.Payments)
                {
                    sale.Payments.Add(new Payment
                    {
                        PaymentId = Guid.NewGuid(),
                        SaleId = sale.SaleId,
                        Method = p.Method,
                        Amount = p.Amount,
                        Currency = p.Currency,
                        MetaJson = p.Meta == null ? null : JsonSerializer.Serialize(p.Meta)
                    });
                }

                _db.Sales.Add(sale);

                // 6) Event log for sync
                var ev = new EventLog
                {
                    TenantId = cmd.TenantId,
                    BranchId = cmd.BranchId,
                    At = DateTimeOffset.UtcNow,
                    Type = "Sale.Committed",
                    EntityType = "Sale",
                    EntityId = sale.SaleId,
                    TerminalId = cmd.TerminalId,
                    RequestId = cmd.RequestId,
                    DataJson = JsonSerializer.Serialize(new
                    {
                        saleId = sale.SaleId,
                        ticket = sale.ServerTicket,
                    total = sale.Total
                    })
                };
                _db.EventLog.Add(ev);

            // Inventory snapshot for the products touched (MVP to keep terminal cache consistent).
            var invRows = await _db.Inventory.AsNoTracking()
                .Where(i => i.TenantId == cmd.TenantId && i.BranchId == cmd.BranchId && productIds.Contains(i.ProductId))
                .Select(i => new { productId = i.ProductId, onHand = i.OnHand, reserved = i.Reserved })
                .ToListAsync(ct);

                // 7) Outbox item for broadcast
                var outbox = new OutboxItem
                {
                    OutboxId = Guid.NewGuid(),
                    TenantId = cmd.TenantId,
                    BranchId = cmd.BranchId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Topic = "multicaja.events",
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        type = ev.Type,
                        tenantId = cmd.TenantId,
                        branchId = cmd.BranchId,
                        seq = 0,
                        at = ev.At,
                    data = new
                    {
                        saleId = sale.SaleId,
                        ticket = sale.ServerTicket,
                        total = sale.Total,
                        inventory = invRows
                    }
                    }),
                    Status = "pending",
                    Attempts = 0
                };
                _db.Outbox.Add(outbox);

                await _db.SaveChangesAsync(ct);
                outbox.PayloadJson = JsonSerializer.Serialize(new
                {
                    type = ev.Type,
                    tenantId = cmd.TenantId,
                    branchId = cmd.BranchId,
                    seq = ev.Seq,
                    at = ev.At,
                data = new
                {
                    saleId = sale.SaleId,
                    ticket = sale.ServerTicket,
                    total = sale.Total,
                    inventory = invRows
                }
                });
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return new(true, "OK", null, sale.SaleId, sale.ServerTicket, ev.Seq);
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync(ct);
            var existing2 = await _db.Sales.AsNoTracking()
                .Where(s => s.TenantId == cmd.TenantId && s.BranchId == cmd.BranchId && s.RequestId == cmd.RequestId)
                .Select(s => new { s.SaleId, s.ServerTicket })
                .FirstOrDefaultAsync(ct);
            if (existing2 != null)
                return new(true, "OK", null, existing2.SaleId, existing2.ServerTicket, null);
            return new(false, "DB_ERROR", ex.Message, null, null, null);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return new(false, "ERROR", ex.Message, null, null, null);
        }
    }
}

