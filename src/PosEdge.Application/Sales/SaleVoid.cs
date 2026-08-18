using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using PosEdge.Domain;
using PosEdge.Infrastructure;

namespace PosEdge.Application.Sales;

public sealed record SaleVoidCommand(
    Guid TenantId,
    Guid BranchId,
    Guid TerminalId,
    Guid CashSessionId,
    Guid SaleId,
    string RequestId,
    string Reason) : IRequest<SaleVoidResult>;

public sealed record SaleVoidResult(
    bool Ok,
    string Code,
    string? Message,
    Guid? JournalId);

public sealed class SaleVoidHandler : IRequestHandler<SaleVoidCommand, SaleVoidResult>
{
    private readonly PosEdgeDbContext _db;
    public SaleVoidHandler(PosEdgeDbContext db) => _db = db;

    public async Task<SaleVoidResult> Handle(SaleVoidCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.RequestId))
            return new(false, "VALIDATION_ERROR", "requestId requerido", null);
        if (cmd.SaleId == Guid.Empty)
            return new(false, "VALIDATION_ERROR", "saleId requerido", null);
        if (string.IsNullOrWhiteSpace(cmd.Reason))
            return new(false, "VALIDATION_ERROR", "reason requerido", null);

        // Idempotency: if journal already exists for this request, return it.
        var existing = await _db.FinancialJournal.AsNoTracking()
            .Where(j => j.TenantId == cmd.TenantId && j.BranchId == cmd.BranchId && j.RequestId == cmd.RequestId)
            .Select(j => j.JournalId)
            .FirstOrDefaultAsync(ct);
        if (existing != Guid.Empty)
            return new(true, "OK", null, existing);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Load sale (with lines/payments) and lock its row for void.
            var sale = await _db.Sales
                .Include(s => s.Lines)
                .Include(s => s.Payments)
                .SingleOrDefaultAsync(s => s.SaleId == cmd.SaleId
                                          && s.TenantId == cmd.TenantId
                                          && s.BranchId == cmd.BranchId, ct);
            if (sale == null)
                return new(false, "NOT_FOUND", "sale no existe", null);

            if (!string.Equals(sale.Status, "committed", StringComparison.OrdinalIgnoreCase))
                return new(false, "INVALID_STATE", $"sale status={sale.Status}", null);

            // Lock inventory rows touched (deterministic order) and revert stock.
            var productIds = sale.Lines.Select(l => l.ProductId).Distinct().OrderBy(x => x).ToArray();
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                SELECT 1
                FROM inventory
                WHERE tenant_id = {cmd.TenantId} AND branch_id = {cmd.BranchId} AND product_id = ANY({productIds})
                ORDER BY product_id
                FOR UPDATE", ct);

            var qtyByProduct = sale.Lines
                .GroupBy(l => l.ProductId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));

            foreach (var (pid, qty) in qtyByProduct)
            {
                await _db.Database.ExecuteSqlInterpolatedAsync($@"
                    UPDATE inventory
                       SET on_hand = on_hand + {qty},
                           updated_at = now()
                     WHERE tenant_id = {cmd.TenantId}
                       AND branch_id = {cmd.BranchId}
                       AND product_id = {pid};", ct);
            }

            // Update sale status (this is mutable table; OK). Financial records remain append-only.
            sale.Status = "voided";
            sale.VoidedAt = DateTimeOffset.UtcNow;
            sale.VoidReason = cmd.Reason;

            // Journal (append-only)
            var j = new FinancialJournal
            {
                JournalId = Guid.NewGuid(),
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                RequestId = cmd.RequestId,
                At = DateTimeOffset.UtcNow,
                Kind = "sale.void",
                RefType = "Sale",
                RefId = sale.SaleId,
                TerminalId = cmd.TerminalId,
                CashSessionId = cmd.CashSessionId,
                Currency = sale.Currency,
                MetaJson = JsonSerializer.Serialize(new { reason = cmd.Reason, voidSaleId = sale.SaleId, ticket = sale.ServerTicket })
            };

            // Double-entry sketch:
            // - Debit: sales_revenue (reverse revenue)
            // - Debit: tax_payable (reverse tax)
            // - Credit: cash/card/... (reverse payments)
            // For MVP we record:
            //   credit total == sale total, debit total == sale total
            var lines = new List<FinancialJournalLine>();
            var ln = 1;
            lines.Add(new FinancialJournalLine { JournalId = j.JournalId, LineNo = ln++, Account = "sales_revenue", Debit = sale.Subtotal, Credit = 0, Note = "reverse sale revenue" });
            lines.Add(new FinancialJournalLine { JournalId = j.JournalId, LineNo = ln++, Account = "tax_payable", Debit = sale.Tax, Credit = 0, Note = "reverse tax" });

            var byMethod = sale.Payments.GroupBy(p => p.Method).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
            foreach (var (method, amt) in byMethod)
            {
                lines.Add(new FinancialJournalLine { JournalId = j.JournalId, LineNo = ln++, Account = $"pay.{method}", Debit = 0, Credit = amt, Note = "reverse payment" });
            }

            j.TotalDebit = lines.Sum(x => x.Debit);
            j.TotalCredit = lines.Sum(x => x.Credit);

            _db.FinancialJournal.Add(j);
            _db.FinancialJournalLines.AddRange(lines);

            // Cash ledger entry (operational cash/session view)
            // If original payments were cash, this represents cash out.
            var cashAmt = byMethod.TryGetValue("cash", out var ca) ? ca : 0m;
            if (cashAmt != 0m)
            {
                await _db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO cash_ledger_entries(entry_id, tenant_id, branch_id, cash_session_id, happened_at, kind, ref_type, ref_id, amount, currency, created_by, note)
                    VALUES (gen_random_uuid(), {cmd.TenantId}, {cmd.BranchId}, {cmd.CashSessionId}, now(),
                            'void', 'Sale', {sale.SaleId}, {-cashAmt}, {sale.Currency}, NULL, {cmd.Reason});", ct);
            }

            // Audit log (append-only)
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO audit_log(audit_id, at, actor_type, actor_id, tenant_id, branch_id, request_id, action, entity_type, entity_id, classification, payload)
                VALUES (gen_random_uuid(), now(), 'terminal', {cmd.TerminalId}, {cmd.TenantId}, {cmd.BranchId},
                        {cmd.RequestId}, 'sale.void', 'Sale', {sale.SaleId}, 'finance',
                        jsonb_build_object('reason', {cmd.Reason}, 'ticket', {sale.ServerTicket}, 'total', {sale.Total}));", ct);

            // Event for sync (terminals learn sale is voided; inventory snapshot is already handled by normal sync on inventory changes)
            _db.EventLog.Add(new EventLog
            {
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                At = DateTimeOffset.UtcNow,
                Type = "Sale.Voided",
                EntityType = "Sale",
                EntityId = sale.SaleId,
                TerminalId = cmd.TerminalId,
                RequestId = cmd.RequestId,
                DataJson = JsonSerializer.Serialize(new { saleId = sale.SaleId, reason = cmd.Reason })
            });

            // Outbox broadcast
            _db.Outbox.Add(new OutboxItem
            {
                OutboxId = Guid.NewGuid(),
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                CreatedAt = DateTimeOffset.UtcNow,
                Topic = "multicaja.events",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    type = "Sale.Voided",
                    tenantId = cmd.TenantId,
                    branchId = cmd.BranchId,
                    seq = 0,
                    at = DateTimeOffset.UtcNow,
                    data = new { saleId = sale.SaleId, reason = cmd.Reason }
                }),
                Status = "pending",
                Attempts = 0
            });

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(true, "OK", null, j.JournalId);
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

