using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using PosEdge.Domain;
using PosEdge.Infrastructure;
using PosEdge.Shared;

namespace PosEdge.Application.Cash;

public sealed record CashSessionOpenCommand(
    Guid TenantId,
    Guid BranchId,
    Guid TerminalId,
    Guid OpenedBy,
    decimal OpeningAmount,
    string RequestId) : IRequest<CashSessionOpenResult>;

public sealed record CashSessionOpenResult(bool Ok, string Code, string? Message, Guid? CashSessionId);

public sealed record CashSessionCloseCommand(
    Guid TenantId,
    Guid BranchId,
    Guid TerminalId,
    Guid CashSessionId,
    Guid ClosedBy,
    string RequestId,
    string? Note) : IRequest<CashSessionCloseResult>;

public sealed record CashSessionCloseResult(bool Ok, string Code, string? Message, Guid? CashSessionId, decimal? ExpectedAmount);

public sealed class CashSessionOpenHandler : IRequestHandler<CashSessionOpenCommand, CashSessionOpenResult>
{
    private readonly PosEdgeDbContext _db;
    public CashSessionOpenHandler(PosEdgeDbContext db) => _db = db;

    public async Task<CashSessionOpenResult> Handle(CashSessionOpenCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.RequestId))
            return new(false, "VALIDATION_ERROR", "requestId requerido", null);

        // Idempotency: open_request_id unique per tenant/branch
        var existing = await _db.CashSessions.AsNoTracking()
            .Where(s => s.TenantId == cmd.TenantId && s.BranchId == cmd.BranchId && s.OpenRequestId == cmd.RequestId)
            .Select(s => (Guid?)s.CashSessionId)
            .FirstOrDefaultAsync(ct);
        if (existing.HasValue)
            return new(true, "OK", null, existing.Value);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // One-open-per-terminal enforced by partial unique index; we also pre-check for clearer error.
            var open = await _db.CashSessions.AsNoTracking()
                .Where(s => s.TenantId == cmd.TenantId && s.BranchId == cmd.BranchId && s.TerminalId == cmd.TerminalId && s.Status == "open")
                .Select(s => s.CashSessionId)
                .FirstOrDefaultAsync(ct);
            if (open != Guid.Empty)
                return new(false, "SESSION_ALREADY_OPEN", "ya existe una cash session abierta", open);

            var cs = new CashSession
            {
                CashSessionId = Guid.NewGuid(),
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                TerminalId = cmd.TerminalId,
                OpenedBy = cmd.OpenedBy,
                OpenedAt = DateTimeOffset.UtcNow,
                OpeningAmount = cmd.OpeningAmount,
                Status = "open",
                OpenRequestId = cmd.RequestId,
                ReconciliationStatus = "balanced"
            };
            _db.CashSessions.Add(cs);

            // Ledger/journal/audit: opening is an operational baseline.
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO cash_ledger_entries(entry_id, tenant_id, branch_id, cash_session_id, happened_at, kind, ref_type, ref_id, amount, currency, created_by, note)
                VALUES (gen_random_uuid(), {cmd.TenantId}, {cmd.BranchId}, {cs.CashSessionId}, now(),
                        'open', 'CashSession', {cs.CashSessionId}, {cmd.OpeningAmount}, 'MXN', {cmd.OpenedBy}, 'opening_amount');", ct);

            _db.FinancialJournal.Add(new FinancialJournal
            {
                JournalId = Guid.NewGuid(),
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                RequestId = cmd.RequestId,
                At = DateTimeOffset.UtcNow,
                Kind = "cash.open",
                RefType = "CashSession",
                RefId = cs.CashSessionId,
                TerminalId = cmd.TerminalId,
                CashSessionId = cs.CashSessionId,
                Currency = "MXN",
                TotalDebit = cmd.OpeningAmount,
                TotalCredit = cmd.OpeningAmount,
                MetaJson = JsonSerializer.Serialize(new { openingAmount = cmd.OpeningAmount })
            });
            _db.FinancialJournalLines.AddRange(
                new FinancialJournalLine { JournalId = _db.FinancialJournal.Local.Last().JournalId, LineNo = 1, Account = "cash", Debit = cmd.OpeningAmount, Credit = 0, Note = "opening cash" },
                new FinancialJournalLine { JournalId = _db.FinancialJournal.Local.Last().JournalId, LineNo = 2, Account = "cash_float", Debit = 0, Credit = cmd.OpeningAmount, Note = "float source" }
            );

            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO audit_log(audit_id, at, actor_type, actor_id, tenant_id, branch_id, request_id, action, entity_type, entity_id, classification, payload)
                VALUES (gen_random_uuid(), now(), 'user', {cmd.OpenedBy}, {cmd.TenantId}, {cmd.BranchId},
                        {cmd.RequestId}, 'cash_session.open', 'CashSession', {cs.CashSessionId}, 'finance',
                        jsonb_build_object('openingAmount', {cmd.OpeningAmount}, 'terminalId', {cmd.TerminalId}));", ct);

            _db.EventLog.Add(new EventLog
            {
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                At = DateTimeOffset.UtcNow,
                Type = "CashSession.Opened",
                EntityType = "CashSession",
                EntityId = cs.CashSessionId,
                TerminalId = cmd.TerminalId,
                RequestId = cmd.RequestId,
                DataJson = JsonSerializer.Serialize(new { cashSessionId = cs.CashSessionId, openingAmount = cmd.OpeningAmount, terminalId = cmd.TerminalId })
            });
            _db.Outbox.Add(new OutboxItem
            {
                OutboxId = Guid.NewGuid(),
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                CreatedAt = DateTimeOffset.UtcNow,
                Topic = "multicaja.events",
                PayloadJson = JsonSerializer.Serialize(new { type = "CashSession.Opened", tenantId = cmd.TenantId, branchId = cmd.BranchId, seq = 0, at = DateTimeOffset.UtcNow, data = new { cashSessionId = cs.CashSessionId, openingAmount = cmd.OpeningAmount, terminalId = cmd.TerminalId } }),
                Status = "pending",
                Attempts = 0
            });

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(true, "OK", null, cs.CashSessionId);
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync(ct);
            return new(false, "DB_ERROR", ex.Message, null);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return new(false, "ERROR", ex.Message, null);
        }
    }
}

public sealed class CashSessionCloseHandler : IRequestHandler<CashSessionCloseCommand, CashSessionCloseResult>
{
    private readonly PosEdgeDbContext _db;
    public CashSessionCloseHandler(PosEdgeDbContext db) => _db = db;

    public async Task<CashSessionCloseResult> Handle(CashSessionCloseCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.RequestId))
            return new(false, "VALIDATION_ERROR", "requestId requerido", null, null);

        // Idempotency: close_request_id unique
        var existing = await _db.CashSessions.AsNoTracking()
            .Where(s => s.TenantId == cmd.TenantId && s.BranchId == cmd.BranchId && s.CloseRequestId == cmd.RequestId)
            .Select(s => new { s.CashSessionId, s.ExpectedAmount })
            .FirstOrDefaultAsync(ct);
        if (existing != null)
            return new(true, "OK", null, existing.CashSessionId, existing.ExpectedAmount);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var cs = await _db.CashSessions.SingleOrDefaultAsync(s =>
                s.CashSessionId == cmd.CashSessionId &&
                s.TenantId == cmd.TenantId &&
                s.BranchId == cmd.BranchId &&
                s.TerminalId == cmd.TerminalId, ct);

            if (cs == null) return new(false, "NOT_FOUND", "cash session no existe", null, null);
            if (!string.Equals(cs.Status, "open", StringComparison.OrdinalIgnoreCase))
                return new(false, "ALREADY_CLOSED", "cash session ya cerrada", cs.CashSessionId, cs.ExpectedAmount);

            // Deterministic expected cash:
            // expected_cash = opening + cash_sales - cash_refunds + cash_movements
            // We model cash_movements through append-only cash_ledger_entries (kind = movement_in/movement_out).
            var salesCash = await _db.Payments.AsNoTracking()
                .Join(_db.Sales.AsNoTracking(), p => p.SaleId, s => s.SaleId, (p, s) => new { p, s })
                .Where(x => x.s.TenantId == cmd.TenantId && x.s.BranchId == cmd.BranchId && x.s.CashSessionId == cmd.CashSessionId)
                .Where(x => x.s.Status == "committed" && x.p.Method == "cash")
                .Select(x => x.p.Amount)
                .SumAsync(ct);

            var refundsCash = await _db.RefundPayments.AsNoTracking()
                .Join(_db.Refunds.AsNoTracking(), rp => rp.RefundId, r => r.RefundId, (rp, r) => new { rp, r })
                .Where(x => x.r.TenantId == cmd.TenantId && x.r.BranchId == cmd.BranchId && x.r.CashSessionId == cmd.CashSessionId)
                .Where(x => x.r.Status == "posted" && x.rp.Method == "cash")
                .Select(x => x.rp.Amount)
                .SumAsync(ct);

            // cash_ledger_entries includes open (+), refunds (negative), voids (negative), and movement adjustments.
            // For close expected, consider movement adjustments only (kind in set) and opening is already in cs.OpeningAmount.
            var movementAdjustments = 0m; // movement endpoints will populate this via cash_ledger_entries; keep 0 for now.

            var expected = cs.OpeningAmount + salesCash - refundsCash + movementAdjustments;
            cs.ExpectedAmount = expected;
            cs.CloseRequestId = cmd.RequestId;
            cs.ClosedBy = cmd.ClosedBy;
            cs.ClosedAt = DateTimeOffset.UtcNow;
            cs.Status = "closed";
            cs.Note = cmd.Note;
            cs.ReconciliationStatus = "discrepancy_open"; // until a count is recorded

            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO audit_log(audit_id, at, actor_type, actor_id, tenant_id, branch_id, request_id, action, entity_type, entity_id, classification, payload)
                VALUES (gen_random_uuid(), now(), 'user', {cmd.ClosedBy}, {cmd.TenantId}, {cmd.BranchId},
                        {cmd.RequestId}, 'cash_session.close', 'CashSession', {cs.CashSessionId}, 'finance',
                        jsonb_build_object('expectedAmount', {expected}, 'terminalId', {cmd.TerminalId}));", ct);

            _db.EventLog.Add(new EventLog
            {
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                At = DateTimeOffset.UtcNow,
                Type = "CashSession.Closed",
                EntityType = "CashSession",
                EntityId = cs.CashSessionId,
                TerminalId = cmd.TerminalId,
                RequestId = cmd.RequestId,
                DataJson = JsonSerializer.Serialize(new { cashSessionId = cs.CashSessionId, expectedAmount = expected })
            });
            _db.Outbox.Add(new OutboxItem
            {
                OutboxId = Guid.NewGuid(),
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                CreatedAt = DateTimeOffset.UtcNow,
                Topic = "multicaja.events",
                PayloadJson = JsonSerializer.Serialize(new { type = "CashSession.Closed", tenantId = cmd.TenantId, branchId = cmd.BranchId, seq = 0, at = DateTimeOffset.UtcNow, data = new { cashSessionId = cs.CashSessionId, expectedAmount = expected } }),
                Status = "pending",
                Attempts = 0
            });

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(true, "OK", null, cs.CashSessionId, expected);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return new(false, "ERROR", ex.Message, null, null);
        }
    }
}

