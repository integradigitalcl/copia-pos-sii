using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PosEdge.Infrastructure;

namespace PosEdge.Workers;

public sealed class ReconciliationScanWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<ReconciliationScanWorker> _log;

    public ReconciliationScanWorker(IServiceProvider sp, ILogger<ReconciliationScanWorker> log)
    {
        _sp = sp;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Safe default: every 60s in prod; tests can set POSEDGE_RECON_SCAN_SECONDS.
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("POSEDGE_RECON_SCAN_SECONDS"), out var s) && s > 0 ? s : 60;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanOnce(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "reconciliation scan error");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken); } catch { }
        }
    }

    public async Task ScanOnce(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PosEdgeDbContext>();

        // MVP scope: deterministic demo tenant/branch; later extend with per-tenant iteration.
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var branchId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        await ScanCashMismatch(db, tenantId, branchId, ct);
        await ScanJournalImbalance(db, tenantId, branchId, ct);
        await ScanOrphans(db, tenantId, branchId, ct);
    }

    private static async Task ScanCashMismatch(PosEdgeDbContext db, Guid tenantId, Guid branchId, CancellationToken ct)
    {
        // Find closed sessions with counts recorded where expected != counted.
        var rows = await db.CashSessions.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.BranchId == branchId)
            .Where(s => s.Status == "closed" && s.CountedAmount != null && s.ExpectedAmount != null)
            .Where(s => s.ReconciliationStatus != "resolved")
            .Select(s => new { s.CashSessionId, s.TerminalId, s.ExpectedAmount, s.CountedAmount, s.DiscrepancyAmount })
            .ToListAsync(ct);

        foreach (var r in rows)
        {
            var expected = r.ExpectedAmount ?? 0m;
            var counted = r.CountedAmount ?? 0m;
            var disc = (r.DiscrepancyAmount ?? (counted - expected));
            if (disc == 0m) continue;
            var abs = Math.Abs(disc);
            var severity = abs >= 200m ? "high" : abs >= 50m ? "medium" : "low";
            var msg = $"cash mismatch session={r.CashSessionId} expected={expected} counted={counted} diff={disc}";

            await InsertFinding(db, tenantId, branchId,
                findingType: "cash_mismatch",
                severity: severity,
                entityType: "CashSession",
                entityId: r.CashSessionId,
                cashSessionId: r.CashSessionId,
                requestId: null,
                message: msg,
                meta: new { expected, counted, discrepancy = disc, terminalId = r.TerminalId },
                ct: ct);
        }
    }

    private static async Task ScanJournalImbalance(PosEdgeDbContext db, Guid tenantId, Guid branchId, CancellationToken ct)
    {
        // Detect journals where total_debit != total_credit (should never happen).
        var bad = await db.FinancialJournal.AsNoTracking()
            .Where(j => j.TenantId == tenantId && j.BranchId == branchId)
            .Where(j => j.TotalDebit != j.TotalCredit)
            .Select(j => new { j.JournalId, j.RequestId, j.TotalDebit, j.TotalCredit, j.Kind })
            .Take(200)
            .ToListAsync(ct);

        foreach (var j in bad)
        {
            var msg = $"journal imbalance journal={j.JournalId} debit={j.TotalDebit} credit={j.TotalCredit} kind={j.Kind}";
            await InsertFinding(db, tenantId, branchId,
                findingType: "journal_imbalance",
                severity: "high",
                entityType: "FinancialJournal",
                entityId: j.JournalId,
                cashSessionId: null,
                requestId: j.RequestId,
                message: msg,
                meta: new { j.TotalDebit, j.TotalCredit, j.Kind },
                ct: ct);
        }
    }

    private static async Task ScanOrphans(PosEdgeDbContext db, Guid tenantId, Guid branchId, CancellationToken ct)
    {
        // Orphan payments: payments referencing missing sale (shouldn't happen due FK; still useful if manual DB edits).
        // We'll scan for orphan outbox dead items.
        var deadOutbox = await db.Outbox.AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.BranchId == branchId && o.Status == "dead")
            .OrderByDescending(o => o.CreatedAt)
            .Take(50)
            .Select(o => new { o.OutboxId, o.LastError, o.CreatedAt })
            .ToListAsync(ct);

        foreach (var o in deadOutbox)
        {
            var msg = $"orphan/dead outbox outboxId={o.OutboxId} err={o.LastError}";
            await InsertFinding(db, tenantId, branchId,
                findingType: "orphan_outbox",
                severity: "medium",
                entityType: "Outbox",
                entityId: o.OutboxId,
                cashSessionId: null,
                requestId: null,
                message: msg,
                meta: new { o.LastError, o.CreatedAt },
                ct: ct);
        }
    }

    private static async Task InsertFinding(PosEdgeDbContext db,
        Guid tenantId,
        Guid branchId,
        string findingType,
        string severity,
        string? entityType,
        Guid? entityId,
        Guid? cashSessionId,
        string? requestId,
        string message,
        object meta,
        CancellationToken ct)
    {
        var fingerprint = Sha256Hex($"{findingType}|{entityType}|{entityId}|{cashSessionId}|{requestId}|{message}");
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO reconciliation_findings(finding_id, tenant_id, branch_id, finding_type, severity, detected_at, status,
                                                    entity_type, entity_id, cash_session_id, request_id, message, meta, fingerprint)
                VALUES (gen_random_uuid(), {tenantId}, {branchId}, {findingType}, {severity}, now(), 'open',
                        {entityType}, {entityId}, {cashSessionId}, {requestId}, {message}, {JsonSerializer.Serialize(meta)}::jsonb, {fingerprint})
                ON CONFLICT (tenant_id, branch_id, fingerprint) DO NOTHING;", ct);
        }
        catch
        {
            // best-effort
        }
    }

    private static string Sha256Hex(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

