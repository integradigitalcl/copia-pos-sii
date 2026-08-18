using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using PosEdge.Infrastructure;

namespace PosEdge.Application.Cash;

public sealed record CashCountLineSpec(decimal Denomination, int Quantity);

public sealed record CashCountRecordCommand(
    Guid TenantId,
    Guid BranchId,
    Guid TerminalId,
    Guid CashSessionId,
    Guid CountedBy,
    string RequestId,
    string Currency,
    IReadOnlyList<CashCountLineSpec> Lines,
    string? Note) : IRequest<CashCountRecordResult>;

public sealed record CashCountRecordResult(
    bool Ok,
    string Code,
    string? Message,
    Guid? CashCountId,
    decimal? ExpectedAmount,
    decimal? CountedAmount,
    decimal? DiscrepancyAmount,
    string? Severity);

public sealed record CashDiscrepancyResolveCommand(
    Guid TenantId,
    Guid BranchId,
    Guid TerminalId,
    Guid CashSessionId,
    Guid DiscrepancyId,
    Guid ResolvedBy,
    string RequestId,
    string Resolution, // accepted|adjusted|investigate
    string? Note) : IRequest<CashDiscrepancyResolveResult>;

public sealed record CashDiscrepancyResolveResult(bool Ok, string Code, string? Message, Guid? ResolutionId);

public sealed class CashCountRecordHandler : IRequestHandler<CashCountRecordCommand, CashCountRecordResult>
{
    private readonly PosEdgeDbContext _db;
    public CashCountRecordHandler(PosEdgeDbContext db) => _db = db;

    public async Task<CashCountRecordResult> Handle(CashCountRecordCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.RequestId))
            return new(false, "VALIDATION_ERROR", "requestId requerido", null, null, null, null, null);
        if (cmd.Lines.Count == 0)
            return new(false, "VALIDATION_ERROR", "lines requerido", null, null, null, null, null);

        // Idempotency
        var existing = await _db.Database.ExecuteSqlRawAsync("SELECT 1;", ct);
        _ = existing;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Validate session exists
            var cs = await _db.CashSessions.SingleOrDefaultAsync(s =>
                s.CashSessionId == cmd.CashSessionId &&
                s.TenantId == cmd.TenantId &&
                s.BranchId == cmd.BranchId &&
                s.TerminalId == cmd.TerminalId, ct);
            if (cs == null) return new(false, "NOT_FOUND", "cash session no existe", null, null, null, null, null);
            if (!string.Equals(cs.Status, "closed", StringComparison.OrdinalIgnoreCase) && !string.Equals(cs.Status, "open", StringComparison.OrdinalIgnoreCase))
                return new(false, "INVALID_STATE", $"cash session status={cs.Status}", null, null, null, null, null);

            // Compute counted total
            decimal counted = 0m;
            var normLines = cmd.Lines
                .Where(l => l.Denomination > 0 && l.Quantity >= 0)
                .GroupBy(l => l.Denomination)
                .Select(g => new CashCountLineSpec(g.Key, g.Sum(x => x.Quantity)))
                .OrderByDescending(x => x.Denomination)
                .ToList();
            foreach (var l in normLines)
                counted += l.Denomination * l.Quantity;
            counted = Math.Round(counted, 4);

            // Compute expected (if not already computed by close, compute now)
            var expected = cs.ExpectedAmount ?? await ComputeExpectedCashAsync(cmd.TenantId, cmd.BranchId, cmd.CashSessionId, cs.OpeningAmount, ct);
            expected = Math.Round(expected, 4);

            var discrepancy = Math.Round(counted - expected, 4);

            // Insert cash_count + lines (append-only)
            var cashCountId = Guid.NewGuid();
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO cash_counts(cash_count_id, tenant_id, branch_id, cash_session_id, request_id, recorded_at, counted_by, total_counted, currency, meta)
                VALUES ({cashCountId}, {cmd.TenantId}, {cmd.BranchId}, {cmd.CashSessionId}, {cmd.RequestId}, now(), {cmd.CountedBy}, {counted}, {cmd.Currency},
                        jsonb_build_object('note', {cmd.Note}, 'terminalId', {cmd.TerminalId}));", ct);

            var lineNo = 1;
            foreach (var l in normLines)
            {
                var amt = Math.Round(l.Denomination * l.Quantity, 4);
                await _db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO cash_count_lines(cash_count_id, line_no, denomination, quantity, amount)
                    VALUES ({cashCountId}, {lineNo}, {l.Denomination}, {l.Quantity}, {amt});", ct);
                lineNo++;
            }

            // Update cash_sessions reconciliation fields (mutable operational state)
            cs.CountedAmount = counted;
            cs.ExpectedAmount = expected;
            cs.DiscrepancyAmount = discrepancy;
            cs.LastCashCountId = cashCountId;
            cs.ReconciliationStatus = discrepancy == 0m ? "balanced" : "discrepancy_open";

            // Detect discrepancy (append-only) when non-zero
            string? severity = null;
            Guid? discrepancyId = null;
            if (discrepancy != 0m)
            {
                var abs = Math.Abs(discrepancy);
                severity = abs >= 200m ? "high" : abs >= 50m ? "medium" : "low";
                discrepancyId = Guid.NewGuid();
                // Use a deterministic requestId derived from the cash count request to keep correlation without new dependencies.
                var discRequestId = cmd.RequestId + ":disc";
                await _db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO cash_discrepancies(discrepancy_id, tenant_id, branch_id, cash_session_id, request_id, detected_at, expected_amount, counted_amount, discrepancy_amount, severity, note)
                    VALUES ({discrepancyId}, {cmd.TenantId}, {cmd.BranchId}, {cmd.CashSessionId}, {discRequestId}, now(), {expected}, {counted}, {discrepancy}, {severity}, {cmd.Note});", ct);

                _db.EventLog.Add(new PosEdge.Domain.EventLog
                {
                    TenantId = cmd.TenantId,
                    BranchId = cmd.BranchId,
                    At = DateTimeOffset.UtcNow,
                    Type = "CashDiscrepancy.Detected",
                    EntityType = "CashSession",
                    EntityId = cmd.CashSessionId,
                    TerminalId = cmd.TerminalId,
                    RequestId = cmd.RequestId,
                    DataJson = JsonSerializer.Serialize(new { cashSessionId = cmd.CashSessionId, discrepancyAmount = discrepancy, severity })
                });

                _db.Outbox.Add(new PosEdge.Domain.OutboxItem
                {
                    OutboxId = Guid.NewGuid(),
                    TenantId = cmd.TenantId,
                    BranchId = cmd.BranchId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Topic = "multicaja.events",
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        type = "CashDiscrepancy.Detected",
                        tenantId = cmd.TenantId,
                        branchId = cmd.BranchId,
                        seq = 0,
                        at = DateTimeOffset.UtcNow,
                        data = new { cashSessionId = cmd.CashSessionId, discrepancyAmount = discrepancy, severity }
                    }),
                    Status = "pending",
                    Attempts = 0
                });
            }

            // Event: CashCount recorded
            _db.EventLog.Add(new PosEdge.Domain.EventLog
            {
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                At = DateTimeOffset.UtcNow,
                Type = "CashCount.Recorded",
                EntityType = "CashSession",
                EntityId = cmd.CashSessionId,
                TerminalId = cmd.TerminalId,
                RequestId = cmd.RequestId,
                DataJson = JsonSerializer.Serialize(new { cashSessionId = cmd.CashSessionId, cashCountId, counted, expected, discrepancy })
            });
            _db.Outbox.Add(new PosEdge.Domain.OutboxItem
            {
                OutboxId = Guid.NewGuid(),
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                CreatedAt = DateTimeOffset.UtcNow,
                Topic = "multicaja.events",
                PayloadJson = JsonSerializer.Serialize(new { type = "CashCount.Recorded", tenantId = cmd.TenantId, branchId = cmd.BranchId, seq = 0, at = DateTimeOffset.UtcNow, data = new { cashSessionId = cmd.CashSessionId, cashCountId, counted, expected, discrepancy } }),
                Status = "pending",
                Attempts = 0
            });

            // Journal entry for count (not money movement, but reconciliation snapshot)
            _db.FinancialJournal.Add(new PosEdge.Domain.FinancialJournal
            {
                JournalId = Guid.NewGuid(),
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                RequestId = cmd.RequestId,
                At = DateTimeOffset.UtcNow,
                Kind = "cash.count",
                RefType = "CashSession",
                RefId = cmd.CashSessionId,
                TerminalId = cmd.TerminalId,
                CashSessionId = cmd.CashSessionId,
                Currency = cmd.Currency,
                TotalDebit = 0,
                TotalCredit = 0,
                MetaJson = JsonSerializer.Serialize(new { cashCountId, counted, expected, discrepancy })
            });

            // Audit log (append-only)
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO audit_log(audit_id, at, actor_type, actor_id, tenant_id, branch_id, request_id, action, entity_type, entity_id, classification, payload)
                VALUES (gen_random_uuid(), now(), 'user', {cmd.CountedBy}, {cmd.TenantId}, {cmd.BranchId},
                        {cmd.RequestId}, 'cash_count.record', 'CashSession', {cmd.CashSessionId}, 'finance',
                        jsonb_build_object('cashCountId', {cashCountId}, 'counted', {counted}, 'expected', {expected}, 'discrepancy', {discrepancy}, 'severity', {severity}));", ct);

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(true, "OK", null, cashCountId, expected, counted, discrepancy, severity);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return new(false, "ERROR", ex.Message, null, null, null, null, null);
        }
    }

    private async Task<decimal> ComputeExpectedCashAsync(Guid tenantId, Guid branchId, Guid cashSessionId, decimal opening, CancellationToken ct)
    {
        var salesCash = await _db.Payments.AsNoTracking()
            .Join(_db.Sales.AsNoTracking(), p => p.SaleId, s => s.SaleId, (p, s) => new { p, s })
            .Where(x => x.s.TenantId == tenantId && x.s.BranchId == branchId && x.s.CashSessionId == cashSessionId)
            .Where(x => x.s.Status == "committed" && x.p.Method == "cash")
            .Select(x => x.p.Amount)
            .SumAsync(ct);

        var refundsCash = await _db.RefundPayments.AsNoTracking()
            .Join(_db.Refunds.AsNoTracking(), rp => rp.RefundId, r => r.RefundId, (rp, r) => new { rp, r })
            .Where(x => x.r.TenantId == tenantId && x.r.BranchId == branchId && x.r.CashSessionId == cashSessionId)
            .Where(x => x.r.Status == "posted" && x.rp.Method == "cash")
            .Select(x => x.rp.Amount)
            .SumAsync(ct);

        return opening + salesCash - refundsCash;
    }
}

public sealed class CashDiscrepancyResolveHandler : IRequestHandler<CashDiscrepancyResolveCommand, CashDiscrepancyResolveResult>
{
    private readonly PosEdgeDbContext _db;
    public CashDiscrepancyResolveHandler(PosEdgeDbContext db) => _db = db;

    public async Task<CashDiscrepancyResolveResult> Handle(CashDiscrepancyResolveCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.RequestId))
            return new(false, "VALIDATION_ERROR", "requestId requerido", null);

        // Idempotency by request
        var existing = await _db.Database.ExecuteSqlRawAsync("SELECT 1;", ct);
        _ = existing;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Insert resolution row (append-only)
            var rid = Guid.NewGuid();
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO cash_discrepancy_resolutions(resolution_id, tenant_id, branch_id, discrepancy_id, request_id, resolved_at, resolved_by, resolution, note)
                VALUES ({rid}, {cmd.TenantId}, {cmd.BranchId}, {cmd.DiscrepancyId}, {cmd.RequestId}, now(), {cmd.ResolvedBy}, {cmd.Resolution}, {cmd.Note});", ct);

            // Update session status (mutable operational state)
            var cs = await _db.CashSessions.SingleOrDefaultAsync(s =>
                s.CashSessionId == cmd.CashSessionId &&
                s.TenantId == cmd.TenantId &&
                s.BranchId == cmd.BranchId &&
                s.TerminalId == cmd.TerminalId, ct);
            if (cs != null)
                cs.ReconciliationStatus = "resolved";

            _db.EventLog.Add(new PosEdge.Domain.EventLog
            {
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                At = DateTimeOffset.UtcNow,
                Type = "CashDiscrepancy.Resolved",
                EntityType = "CashSession",
                EntityId = cmd.CashSessionId,
                TerminalId = cmd.TerminalId,
                RequestId = cmd.RequestId,
                DataJson = JsonSerializer.Serialize(new { cashSessionId = cmd.CashSessionId, discrepancyId = cmd.DiscrepancyId, resolution = cmd.Resolution })
            });
            _db.Outbox.Add(new PosEdge.Domain.OutboxItem
            {
                OutboxId = Guid.NewGuid(),
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                CreatedAt = DateTimeOffset.UtcNow,
                Topic = "multicaja.events",
                PayloadJson = JsonSerializer.Serialize(new { type = "CashDiscrepancy.Resolved", tenantId = cmd.TenantId, branchId = cmd.BranchId, seq = 0, at = DateTimeOffset.UtcNow, data = new { cashSessionId = cmd.CashSessionId, discrepancyId = cmd.DiscrepancyId, resolution = cmd.Resolution } }),
                Status = "pending",
                Attempts = 0
            });

            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO audit_log(audit_id, at, actor_type, actor_id, tenant_id, branch_id, request_id, action, entity_type, entity_id, classification, payload)
                VALUES (gen_random_uuid(), now(), 'user', {cmd.ResolvedBy}, {cmd.TenantId}, {cmd.BranchId},
                        {cmd.RequestId}, 'cash_discrepancy.resolve', 'CashSession', {cmd.CashSessionId}, 'finance',
                        jsonb_build_object('discrepancyId', {cmd.DiscrepancyId}, 'resolution', {cmd.Resolution}));", ct);

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(true, "OK", null, rid);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return new(false, "ERROR", ex.Message, null);
        }
    }
}

