using MediatR;
using Microsoft.EntityFrameworkCore;
using PosEdge.Domain;
using PosEdge.Infrastructure;

namespace PosEdge.Application.Leases;

public sealed class LeaseRequestHandler : IRequestHandler<LeaseRequestCommand, LeaseResponse>
{
    private readonly PosEdgeDbContext _db;
    public LeaseRequestHandler(PosEdgeDbContext db) => _db = db;

    public async Task<LeaseResponse> Handle(LeaseRequestCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.RequestId))
            return new(false, "VALIDATION_ERROR", "requestId requerido", null, null, null);
        if (cmd.Lines.Count == 0)
            return new(false, "VALIDATION_ERROR", "lines vacío", null, null, null);

        var ttl = Math.Clamp(cmd.TtlSeconds, 5, 600);

        // Idempotency: same requestId returns same lease.
        var existing = await _db.InventoryLeases
            .AsNoTracking()
            .Include(l => l.Lines)
            .Where(l => l.TenantId == cmd.TenantId && l.BranchId == cmd.BranchId && l.RequestId == cmd.RequestId)
            .FirstOrDefaultAsync(ct);
        if (existing != null)
        {
            return new(true, "OK", null, existing.LeaseId, existing.ExpiresAt,
                existing.Lines.Select(x => new LeaseLineDto(x.ProductId, x.QtyAllocated, x.QtyUsed)).ToList());
        }

        // Locking strategy: READ COMMITTED + row locks on inventory rows we touch + inventory.reserved accounting.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var productIds = cmd.Lines.Select(x => x.ProductId).Distinct().OrderBy(x => x).ToArray();

            // Lock inventory rows for these products to prevent concurrent overallocation.
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                SELECT 1
                  FROM inventory
                 WHERE tenant_id = {cmd.TenantId} AND branch_id = {cmd.BranchId} AND product_id = ANY({productIds})
                 ORDER BY product_id
                 FOR UPDATE", ct);

            // Validate availability = on_hand - reserved.
            foreach (var line in cmd.Lines)
            {
                if (line.Qty <= 0)
                    return new(false, "VALIDATION_ERROR", $"qty inválida {line.ProductId}", null, null, null);

                var avail = await _db.Inventory.AsNoTracking()
                    .Where(i => i.TenantId == cmd.TenantId && i.BranchId == cmd.BranchId && i.ProductId == line.ProductId)
                    .Select(i => i.OnHand - i.Reserved)
                    .SingleOrDefaultAsync(ct);

                if (avail < line.Qty)
                {
                    await tx.RollbackAsync(ct);
                    return new(false, "INSUFFICIENT_STOCK", $"No hay stock disponible para lease {line.ProductId}", null, null, null);
                }
            }

            // Reserve stock on inventory rows.
            foreach (var line in cmd.Lines)
            {
                var rows = await _db.Database.ExecuteSqlInterpolatedAsync($@"
                    UPDATE inventory
                       SET reserved = reserved + {line.Qty},
                           updated_at = now()
                     WHERE tenant_id = {cmd.TenantId}
                       AND branch_id = {cmd.BranchId}
                       AND product_id = {line.ProductId}
                       AND (on_hand - reserved) >= {line.Qty}", ct);
                if (rows == 0)
                {
                    await tx.RollbackAsync(ct);
                    return new(false, "INSUFFICIENT_STOCK", $"No se pudo reservar (carrera) {line.ProductId}", null, null, null);
                }
            }

            var lease = new InventoryLease
            {
                LeaseId = Guid.NewGuid(),
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                TerminalId = cmd.TerminalId,
                RequestId = cmd.RequestId,
                Status = "active",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(ttl)
            };
            foreach (var line in cmd.Lines)
            {
                lease.Lines.Add(new InventoryLeaseLine
                {
                    LeaseId = lease.LeaseId,
                    ProductId = line.ProductId,
                    QtyAllocated = line.Qty,
                    QtyUsed = 0
                });
            }
            _db.InventoryLeases.Add(lease);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new(true, "OK", null, lease.LeaseId, lease.ExpiresAt,
                lease.Lines.Select(x => new LeaseLineDto(x.ProductId, x.QtyAllocated, x.QtyUsed)).ToList());
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return new(false, "ERROR", ex.Message, null, null, null);
        }
    }
}

public sealed class LeaseRenewHandler : IRequestHandler<LeaseRenewCommand, LeaseResponse>
{
    private readonly PosEdgeDbContext _db;
    public LeaseRenewHandler(PosEdgeDbContext db) => _db = db;

    public async Task<LeaseResponse> Handle(LeaseRenewCommand cmd, CancellationToken ct)
    {
        var extend = Math.Clamp(cmd.ExtendSeconds, 5, 600);
        if (string.IsNullOrWhiteSpace(cmd.RequestId))
            return new(false, "VALIDATION_ERROR", "requestId requerido", null, null, null);

        // Idempotency for renew: if a lease exists with same requestId, return it.
        var existing = await _db.InventoryLeases.AsNoTracking()
            .Include(l => l.Lines)
            .Where(l => l.TenantId == cmd.TenantId && l.BranchId == cmd.BranchId && l.RequestId == cmd.RequestId)
            .FirstOrDefaultAsync(ct);
        if (existing != null)
        {
            return new(true, "OK", null, existing.LeaseId, existing.ExpiresAt,
                existing.Lines.Select(x => new LeaseLineDto(x.ProductId, x.QtyAllocated, x.QtyUsed)).ToList());
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var lease = await _db.InventoryLeases
                .Include(l => l.Lines)
                .SingleOrDefaultAsync(l => l.LeaseId == cmd.LeaseId
                                           && l.TenantId == cmd.TenantId
                                           && l.BranchId == cmd.BranchId
                                           && l.TerminalId == cmd.TerminalId, ct);
            if (lease == null)
                return new(false, "NOT_FOUND", "lease no existe", null, null, null);
            if (!string.Equals(lease.Status, "active", StringComparison.OrdinalIgnoreCase))
                return new(false, "LEASE_INVALID", $"lease status={lease.Status}", lease.LeaseId, lease.ExpiresAt,
                    lease.Lines.Select(x => new LeaseLineDto(x.ProductId, x.QtyAllocated, x.QtyUsed)).ToList());

            // If already expired, mark it and reject renew.
            if (lease.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                lease.Status = "expired";
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new(false, "LEASE_EXPIRED", "lease expirado", lease.LeaseId, lease.ExpiresAt,
                    lease.Lines.Select(x => new LeaseLineDto(x.ProductId, x.QtyAllocated, x.QtyUsed)).ToList());
            }

            lease.ExpiresAt = lease.ExpiresAt.AddSeconds(extend);
            lease.RenewedAt = DateTimeOffset.UtcNow;
            lease.RequestId = cmd.RequestId; // store latest renew idempotency key
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new(true, "OK", null, lease.LeaseId, lease.ExpiresAt,
                lease.Lines.Select(x => new LeaseLineDto(x.ProductId, x.QtyAllocated, x.QtyUsed)).ToList());
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return new(false, "ERROR", ex.Message, null, null, null);
        }
    }
}

public sealed class LeaseRevokeHandler : IRequestHandler<LeaseRevokeCommand, LeaseRevokeResponse>
{
    private readonly PosEdgeDbContext _db;
    public LeaseRevokeHandler(PosEdgeDbContext db) => _db = db;

    public async Task<LeaseRevokeResponse> Handle(LeaseRevokeCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.RequestId))
            return new(false, "VALIDATION_ERROR", "requestId requerido", null, null);
        if (cmd.LeaseId == Guid.Empty)
            return new(false, "VALIDATION_ERROR", "leaseId requerido", null, null);

        // Idempotency: same requestId for same lease returns the same result.
        var existing = await _db.EventLog.AsNoTracking()
            .Where(e => e.TenantId == cmd.TenantId && e.BranchId == cmd.BranchId && e.Type == "Lease.Revoked" && e.RequestId == cmd.RequestId)
            .OrderByDescending(e => e.Seq)
            .Select(e => new { e.Seq, e.EntityId })
            .FirstOrDefaultAsync(ct);
        if (existing != null)
            return new(true, "OK", null, existing.EntityId, existing.Seq);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var lease = await _db.InventoryLeases
                .Include(l => l.Lines)
                .SingleOrDefaultAsync(l => l.LeaseId == cmd.LeaseId
                                           && l.TenantId == cmd.TenantId
                                           && l.BranchId == cmd.BranchId, ct);
            if (lease == null)
                return new(false, "NOT_FOUND", "lease no existe", null, null);

            if (cmd.TerminalId.HasValue && lease.TerminalId != cmd.TerminalId.Value)
                return new(false, "INVALID_TERMINAL", "terminal no coincide", lease.LeaseId, null);

            if (string.Equals(lease.Status, "revoked", StringComparison.OrdinalIgnoreCase))
            {
                await tx.CommitAsync(ct);
                return new(true, "OK", null, lease.LeaseId, null);
            }

            // Mark revoked
            lease.Status = "revoked";
            lease.RevokedAt = DateTimeOffset.UtcNow;
            lease.Note = cmd.Note;

            // Release reserved remaining = allocated - used
            foreach (var line in lease.Lines)
            {
                var remaining = line.QtyAllocated - line.QtyUsed;
                if (remaining <= 0) continue;
                await _db.Database.ExecuteSqlInterpolatedAsync($@"
                    UPDATE inventory
                       SET reserved = GREATEST(0, reserved - {remaining}),
                           updated_at = now()
                     WHERE tenant_id = {lease.TenantId}
                       AND branch_id = {lease.BranchId}
                       AND product_id = {line.ProductId};", ct);
            }

            // Audit event
            var ev = new EventLog
            {
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                At = DateTimeOffset.UtcNow,
                Type = "Lease.Revoked",
                EntityType = "InventoryLease",
                EntityId = lease.LeaseId,
                TerminalId = lease.TerminalId,
                RequestId = cmd.RequestId,
                DataJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    leaseId = lease.LeaseId,
                    terminalId = lease.TerminalId,
                    reason = cmd.Reason,
                    note = cmd.Note,
                    revokedAt = lease.RevokedAt
                })
            };
            _db.EventLog.Add(ev);

            // Outbox broadcast
            var outbox = new OutboxItem
            {
                OutboxId = Guid.NewGuid(),
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                CreatedAt = DateTimeOffset.UtcNow,
                Topic = "multicaja.events",
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = ev.Type,
                    tenantId = cmd.TenantId,
                    branchId = cmd.BranchId,
                    seq = 0,
                    at = ev.At,
                    data = new
                    {
                        leaseId = lease.LeaseId,
                        terminalId = lease.TerminalId,
                        reason = cmd.Reason
                    }
                }),
                Status = "pending",
                Attempts = 0
            };
            _db.Outbox.Add(outbox);

            await _db.SaveChangesAsync(ct);

            // Patch seq into outbox payload
            outbox.PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                type = ev.Type,
                tenantId = cmd.TenantId,
                branchId = cmd.BranchId,
                seq = ev.Seq,
                at = ev.At,
                data = new
                {
                    leaseId = lease.LeaseId,
                    terminalId = lease.TerminalId,
                    reason = cmd.Reason
                }
            });
            await _db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);
            return new(true, "OK", null, lease.LeaseId, ev.Seq);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return new(false, "ERROR", ex.Message, null, null);
        }
    }
}

