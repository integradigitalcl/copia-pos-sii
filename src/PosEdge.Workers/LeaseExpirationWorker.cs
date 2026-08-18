using Microsoft.EntityFrameworkCore;
using PosEdge.Infrastructure;

namespace PosEdge.Workers;

/// <summary>
/// Reclaims reserved stock for expired/revoked leases.
/// This is critical to avoid "reserved leak".
/// </summary>
public sealed class LeaseExpirationWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<LeaseExpirationWorker> _log;

    public LeaseExpirationWorker(IServiceProvider sp, ILogger<LeaseExpirationWorker> log)
    {
        _sp = sp;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReclaimOnce(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "lease expiration loop error");
            }

            try { await Task.Delay(2000, stoppingToken); } catch { }
        }
    }

    private async Task ReclaimOnce(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PosEdgeDbContext>();

        // Find expired active leases.
        var now = DateTimeOffset.UtcNow;
        var leases = await db.InventoryLeases
            .Include(l => l.Lines)
            .Where(l => l.Status == "active" && l.ExpiresAt <= now)
            .OrderBy(l => l.ExpiresAt)
            .Take(50)
            .ToListAsync(ct);

        if (leases.Count == 0)
            return;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var lease in leases)
        {
            // Mark expired
            lease.Status = "expired";

            // Release reserved = allocated - used
            foreach (var line in lease.Lines)
            {
                var remaining = line.QtyAllocated - line.QtyUsed;
                if (remaining <= 0) continue;
                var rows = await db.Database.ExecuteSqlInterpolatedAsync($@"
                    UPDATE inventory
                       SET reserved = GREATEST(0, reserved - {remaining}),
                           updated_at = now()
                     WHERE tenant_id = {lease.TenantId}
                       AND branch_id = {lease.BranchId}
                       AND product_id = {line.ProductId};", ct);
                _ = rows;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _log.LogInformation("Expired leases reclaimed: {Count}", leases.Count);
    }
}

