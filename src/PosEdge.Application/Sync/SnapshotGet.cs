using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.EntityFrameworkCore;
using PosEdge.Domain;
using PosEdge.Infrastructure;

namespace PosEdge.Application.Sync;

public sealed record SnapshotGetQuery(
    Guid TenantId,
    Guid BranchId,
    bool HashOnly,
    bool IncludeLeases) : IRequest<SnapshotGetResult>;

public sealed record SnapshotGetResult(
    bool Ok,
    string Code,
    string? Message,
    long BaseSeq,
    DateTimeOffset GeneratedAt,
    string InventoryHash,
    string ProductsHash,
    string LeasesHash,
    string SnapshotHash,
    string SnapshotHashNoLeases,
    IReadOnlyList<SnapshotProductRow>? Products,
    IReadOnlyList<SnapshotInventoryRow>? Inventory,
    IReadOnlyList<SnapshotLeaseRow>? Leases);

public sealed record SnapshotProductRow(Guid ProductId, string Sku, string Name, decimal Price, decimal TaxRate);
public sealed record SnapshotInventoryRow(Guid ProductId, decimal OnHand, decimal Reserved);
public sealed record SnapshotLeaseRow(Guid LeaseId, Guid TerminalId, string Status, DateTimeOffset ExpiresAt, IReadOnlyList<SnapshotLeaseLineRow> Lines);
public sealed record SnapshotLeaseLineRow(Guid ProductId, decimal QtyAllocated, decimal QtyUsed);

public sealed class SnapshotGetHandler : IRequestHandler<SnapshotGetQuery, SnapshotGetResult>
{
    private readonly PosEdgeDbContext _db;
    public SnapshotGetHandler(PosEdgeDbContext db) => _db = db;

    public async Task<SnapshotGetResult> Handle(SnapshotGetQuery q, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var baseSeq = await _db.EventLog.AsNoTracking()
            .Where(e => e.TenantId == q.TenantId && e.BranchId == q.BranchId)
            .Select(e => (long?)e.Seq)
            .MaxAsync(ct) ?? 0L;

        // Hashes must be deterministic.
        var invRows = await _db.Inventory.AsNoTracking()
            .Where(i => i.TenantId == q.TenantId && i.BranchId == q.BranchId)
            .OrderBy(i => i.ProductId)
            .Select(i => new SnapshotInventoryRow(i.ProductId, i.OnHand, i.Reserved))
            .ToListAsync(ct);

        var prodRows = await _db.Products.AsNoTracking()
            .Where(p => p.TenantId == q.TenantId && p.DeletedAt == null)
            .OrderBy(p => p.ProductId)
            .Select(p => new SnapshotProductRow(p.ProductId, p.Sku, p.Name, p.Price, p.TaxRate))
            .ToListAsync(ct);

        List<SnapshotLeaseRow> leaseRows = new();
        if (q.IncludeLeases)
        {
            var leases = await _db.InventoryLeases.AsNoTracking()
                .Include(l => l.Lines)
                .Where(l => l.TenantId == q.TenantId && l.BranchId == q.BranchId && l.Status == "active")
                .OrderBy(l => l.LeaseId)
                .ToListAsync(ct);

            leaseRows = leases.Select(l =>
                    new SnapshotLeaseRow(
                        l.LeaseId,
                        l.TerminalId,
                        l.Status,
                        l.ExpiresAt,
                        l.Lines.OrderBy(x => x.ProductId)
                            .Select(x => new SnapshotLeaseLineRow(x.ProductId, x.QtyAllocated, x.QtyUsed))
                            .ToList()))
                .ToList();
        }

        var inventoryHash = Sha256Hex(SerializeInventory(invRows));
        var productsHash = Sha256Hex(SerializeProducts(prodRows));
        var leasesHash = q.IncludeLeases ? Sha256Hex(SerializeLeases(leaseRows)) : Sha256Hex("");
        var snapshotHashNoLeases = Sha256Hex($"{baseSeq}|{inventoryHash}|{productsHash}");
        var snapshotHash = Sha256Hex($"{baseSeq}|{inventoryHash}|{productsHash}|{leasesHash}");

        return new SnapshotGetResult(
            true,
            "OK",
            null,
            baseSeq,
            now,
            inventoryHash,
            productsHash,
            leasesHash,
            snapshotHash,
            snapshotHashNoLeases,
            q.HashOnly ? null : prodRows,
            q.HashOnly ? null : invRows,
            q.HashOnly ? null : leaseRows);
    }

    private static string SerializeInventory(IEnumerable<SnapshotInventoryRow> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.Append(r.ProductId.ToString("D")).Append('|')
                .Append(r.OnHand.ToString("0.####", CultureInfo.InvariantCulture)).Append('|')
                .Append(r.Reserved.ToString("0.####", CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }

    private static string SerializeProducts(IEnumerable<SnapshotProductRow> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.Append(r.ProductId.ToString("D")).Append('|')
                .Append(r.Sku).Append('|')
                .Append(r.Name).Append('|')
                .Append(r.Price.ToString("0.####", CultureInfo.InvariantCulture)).Append('|')
                .Append(r.TaxRate.ToString("0.####", CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }

    private static string SerializeLeases(IEnumerable<SnapshotLeaseRow> rows)
    {
        var sb = new StringBuilder();
        foreach (var l in rows)
        {
            sb.Append(l.LeaseId.ToString("D")).Append('|')
                .Append(l.TerminalId.ToString("D")).Append('|')
                .Append(l.Status).Append('|')
                .Append(l.ExpiresAt.ToUnixTimeMilliseconds()).Append('\n');
            foreach (var ln in l.Lines)
            {
                sb.Append("  ")
                    .Append(ln.ProductId.ToString("D")).Append('|')
                    .Append(ln.QtyAllocated.ToString("0.####", CultureInfo.InvariantCulture)).Append('|')
                    .Append(ln.QtyUsed.ToString("0.####", CultureInfo.InvariantCulture)).Append('\n');
            }
        }
        return sb.ToString();
    }

    private static string Sha256Hex(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

