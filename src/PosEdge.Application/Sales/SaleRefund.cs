using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using PosEdge.Domain;
using PosEdge.Infrastructure;

namespace PosEdge.Application.Sales;

public sealed record RefundLineSpec(Guid SaleLineId, decimal Qty);
public sealed record RefundPaymentSpec(string Method, decimal Amount, string Currency, object? Meta);

public sealed record SaleRefundCommand(
    Guid TenantId,
    Guid BranchId,
    Guid TerminalId,
    Guid CashSessionId,
    Guid SaleId,
    string RequestId,
    string Kind, // refund|credit_note
    IReadOnlyList<RefundLineSpec> Lines,
    IReadOnlyList<RefundPaymentSpec>? Payments,
    string? Note) : IRequest<SaleRefundResult>;

public sealed record SaleRefundResult(
    bool Ok,
    string Code,
    string? Message,
    Guid? RefundId,
    Guid? JournalId,
    long? EventSeq);

public sealed class SaleRefundHandler : IRequestHandler<SaleRefundCommand, SaleRefundResult>
{
    private readonly PosEdgeDbContext _db;
    public SaleRefundHandler(PosEdgeDbContext db) => _db = db;

    public async Task<SaleRefundResult> Handle(SaleRefundCommand cmd, CancellationToken ct)
    {
        var kind = (cmd.Kind ?? "refund").Trim().ToLowerInvariant();
        if (kind is not ("refund" or "credit_note"))
            return new(false, "VALIDATION_ERROR", "kind inválido", null, null, null);
        if (string.IsNullOrWhiteSpace(cmd.RequestId))
            return new(false, "VALIDATION_ERROR", "requestId requerido", null, null, null);
        if (cmd.SaleId == Guid.Empty)
            return new(false, "VALIDATION_ERROR", "saleId requerido", null, null, null);
        if (cmd.Lines.Count == 0)
            return new(false, "VALIDATION_ERROR", "lines requerido", null, null, null);

        // Idempotency: if refund exists for requestId return it.
        var existing = await _db.Refunds.AsNoTracking()
            .Where(r => r.TenantId == cmd.TenantId && r.BranchId == cmd.BranchId && r.RequestId == cmd.RequestId)
            .Select(r => new { r.RefundId })
            .FirstOrDefaultAsync(ct);
        if (existing != null)
        {
            var j = await _db.FinancialJournal.AsNoTracking()
                .Where(x => x.TenantId == cmd.TenantId && x.BranchId == cmd.BranchId && x.RequestId == cmd.RequestId)
                .Select(x => (Guid?)x.JournalId)
                .FirstOrDefaultAsync(ct);
            var ev = await _db.EventLog.AsNoTracking()
                .Where(e => e.TenantId == cmd.TenantId && e.BranchId == cmd.BranchId && e.RequestId == cmd.RequestId)
                .OrderByDescending(e => e.Seq)
                .Select(e => (long?)e.Seq)
                .FirstOrDefaultAsync(ct);
            return new(true, "OK", null, existing.RefundId, j, ev);
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Load sale and validate
            var sale = await _db.Sales
                .Include(s => s.Lines)
                .Include(s => s.Payments)
                .SingleOrDefaultAsync(s => s.SaleId == cmd.SaleId
                                          && s.TenantId == cmd.TenantId
                                          && s.BranchId == cmd.BranchId, ct);
            if (sale == null)
                return new(false, "NOT_FOUND", "sale no existe", null, null, null);
            if (!string.Equals(sale.Status, "committed", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(sale.Status, "voided", StringComparison.OrdinalIgnoreCase))
            {
                // For now we allow refund of committed only; voided is already reversed and should not be refunded.
                return new(false, "INVALID_STATE", $"sale status={sale.Status}", null, null, null);
            }
            if (string.Equals(sale.Status, "voided", StringComparison.OrdinalIgnoreCase))
                return new(false, "INVALID_STATE", "sale ya voided; no reembolsable", null, null, null);

            // Resolve requested refund quantities by saleLineId
            var reqQty = cmd.Lines
                .GroupBy(x => x.SaleLineId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));
            if (reqQty.Values.Any(q => q <= 0))
                return new(false, "VALIDATION_ERROR", "qty inválida", null, null, null);

            // Compute already-refunded quantities (posted refunds only)
            var saleLineIds = reqQty.Keys.ToArray();
            var already = await _db.RefundLines.AsNoTracking()
                .Join(_db.Refunds.AsNoTracking(),
                    rl => rl.RefundId,
                    r => r.RefundId,
                    (rl, r) => new { rl, r })
                .Where(x => x.r.TenantId == cmd.TenantId && x.r.BranchId == cmd.BranchId && x.r.SaleId == cmd.SaleId && x.r.Status == "posted")
                .Where(x => saleLineIds.Contains(x.rl.SaleLineId))
                .GroupBy(x => x.rl.SaleLineId)
                .Select(g => new { SaleLineId = g.Key, RefQty = g.Sum(z => z.rl.Qty) })
                .ToListAsync(ct);
            var refundedByLine = already.ToDictionary(x => x.SaleLineId, x => x.RefQty);

            // Validate against original sale lines
            var saleLinesById = sale.Lines.ToDictionary(l => l.SaleLineId);
            foreach (var (slid, qty) in reqQty)
            {
                if (!saleLinesById.TryGetValue(slid, out var sl))
                    return new(false, "VALIDATION_ERROR", $"sale_line no existe {slid}", null, null, null);

                var alreadyQty = refundedByLine.TryGetValue(slid, out var aq) ? aq : 0m;
                var remaining = sl.Qty - alreadyQty;
                if (remaining < qty)
                    return new(false, "ALREADY_REFUNDED", $"excede remaining sale_line={slid} remaining={remaining}", null, null, null);
            }

            // Lock inventory rows touched and apply stock back
            var productIds = reqQty.Keys.Select(slid => saleLinesById[slid].ProductId).Distinct().OrderBy(x => x).ToArray();
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                SELECT 1
                FROM inventory
                WHERE tenant_id = {cmd.TenantId} AND branch_id = {cmd.BranchId} AND product_id = ANY({productIds})
                ORDER BY product_id
                FOR UPDATE", ct);

            var qtyByProduct = reqQty
                .GroupBy(kv => saleLinesById[kv.Key].ProductId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Value));
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

            // Compute totals from original line pricing/tax (proportional by qty)
            decimal subtotal = 0, tax = 0, total = 0;
            var refundLines = new List<RefundLine>();
            foreach (var (slid, qty) in reqQty)
            {
                var sl = saleLinesById[slid];
                var lineSubtotal = sl.UnitPrice * qty;
                var lineTax = lineSubtotal * sl.TaxRate;
                var lineTotal = lineSubtotal + lineTax;
                subtotal += lineSubtotal;
                tax += lineTax;
                total += lineTotal;
                refundLines.Add(new RefundLine
                {
                    RefundId = Guid.Empty, // set after refund created
                    SaleLineId = sl.SaleLineId,
                    ProductId = sl.ProductId,
                    Qty = qty,
                    UnitPrice = sl.UnitPrice,
                    TaxRate = sl.TaxRate,
                    LineSubtotal = lineSubtotal,
                    LineTax = lineTax,
                    LineTotal = lineTotal
                });
            }

            // Payments: if caller didn't specify, default mirror original sale payment mix proportionally by amount share.
            List<RefundPayment> refundPayments = new();
            if (cmd.Payments != null && cmd.Payments.Count > 0)
            {
                if (cmd.Payments.Sum(p => p.Amount) != total)
                {
                    // Keep strict for now to avoid hidden imbalances.
                    return new(false, "VALIDATION_ERROR", "payments deben sumar total refund", null, null, null);
                }
                refundPayments = cmd.Payments.Select(p => new RefundPayment
                {
                    RefundPaymentId = Guid.NewGuid(),
                    RefundId = Guid.Empty,
                    Method = p.Method,
                    Amount = p.Amount,
                    Currency = p.Currency,
                    MetaJson = p.Meta == null ? null : JsonSerializer.Serialize(p.Meta)
                }).ToList();
            }
            else
            {
                var salePayTotal = sale.Payments.Sum(p => p.Amount);
                if (salePayTotal <= 0) return new(false, "VALIDATION_ERROR", "sale payments inválidos", null, null, null);
                foreach (var g in sale.Payments.GroupBy(p => p.Method))
                {
                    var methodAmt = g.Sum(x => x.Amount);
                    var ratio = methodAmt / salePayTotal;
                    var amt = Math.Round(total * ratio, 4);
                    if (amt <= 0) continue;
                    refundPayments.Add(new RefundPayment
                    {
                        RefundPaymentId = Guid.NewGuid(),
                        RefundId = Guid.Empty,
                        Method = g.Key,
                        Amount = amt,
                        Currency = sale.Currency,
                        MetaJson = null
                    });
                }
                // Rounding fix: adjust cash if exists, else first method
                var diff = total - refundPayments.Sum(p => p.Amount);
                if (diff != 0 && refundPayments.Count > 0)
                {
                    var idx = refundPayments.FindIndex(p => p.Method == "cash");
                    if (idx < 0) idx = 0;
                    refundPayments[idx].Amount += diff;
                }
            }

            var refund = new Refund
            {
                RefundId = Guid.NewGuid(),
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                SaleId = sale.SaleId,
                RequestId = cmd.RequestId,
                TerminalId = cmd.TerminalId,
                CashSessionId = cmd.CashSessionId,
                Kind = kind,
                Status = "posted",
                CreatedAt = DateTimeOffset.UtcNow,
                Currency = sale.Currency,
                Subtotal = subtotal,
                Tax = tax,
                Total = total,
                Note = cmd.Note
            };

            foreach (var rl in refundLines) rl.RefundId = refund.RefundId;
            foreach (var rp in refundPayments) rp.RefundId = refund.RefundId;
            refund.Lines = refundLines;
            refund.Payments = refundPayments;

            _db.Refunds.Add(refund);

            // Financial journal (append-only): reverse payments (debit pay.*) and credit refunds/contra-revenue.
            var j = new FinancialJournal
            {
                JournalId = Guid.NewGuid(),
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                RequestId = cmd.RequestId,
                At = DateTimeOffset.UtcNow,
                Kind = kind == "credit_note" ? "credit_note" : "refund",
                RefType = "Refund",
                RefId = refund.RefundId,
                TerminalId = cmd.TerminalId,
                CashSessionId = cmd.CashSessionId,
                Currency = sale.Currency,
                MetaJson = JsonSerializer.Serialize(new { saleId = sale.SaleId, refundId = refund.RefundId, note = cmd.Note })
            };

            var jl = new List<FinancialJournalLine>();
            var lnNo = 1;
            // Reverse revenue and tax as credits (contra accounts)
            jl.Add(new FinancialJournalLine { JournalId = j.JournalId, LineNo = lnNo++, Account = "sales_revenue", Debit = 0, Credit = subtotal, Note = "refund reduces revenue" });
            jl.Add(new FinancialJournalLine { JournalId = j.JournalId, LineNo = lnNo++, Account = "tax_payable", Debit = 0, Credit = tax, Note = "refund reduces tax" });
            foreach (var p in refundPayments.GroupBy(x => x.Method).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount)))
            {
                jl.Add(new FinancialJournalLine { JournalId = j.JournalId, LineNo = lnNo++, Account = $"pay.{p.Key}", Debit = p.Value, Credit = 0, Note = "refund payment out" });
            }
            j.TotalDebit = jl.Sum(x => x.Debit);
            j.TotalCredit = jl.Sum(x => x.Credit);

            _db.FinancialJournal.Add(j);
            _db.FinancialJournalLines.AddRange(jl);

            // Cash ledger: if cash refund, record cash out (negative)
            var cash = refundPayments.Where(p => p.Method == "cash").Sum(p => p.Amount);
            if (cash != 0m)
            {
                await _db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO cash_ledger_entries(entry_id, tenant_id, branch_id, cash_session_id, happened_at, kind, ref_type, ref_id, amount, currency, created_by, note)
                    VALUES (gen_random_uuid(), {cmd.TenantId}, {cmd.BranchId}, {cmd.CashSessionId}, now(),
                            'refund', 'Refund', {refund.RefundId}, {-cash}, {sale.Currency}, NULL, {cmd.Note});", ct);
            }

            // Audit log
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO audit_log(audit_id, at, actor_type, actor_id, tenant_id, branch_id, request_id, action, entity_type, entity_id, classification, payload)
                VALUES (gen_random_uuid(), now(), 'terminal', {cmd.TerminalId}, {cmd.TenantId}, {cmd.BranchId},
                        {cmd.RequestId}, 'sale.refund', 'Refund', {refund.RefundId}, 'finance',
                        jsonb_build_object('saleId', {sale.SaleId}, 'refundId', {refund.RefundId}, 'total', {total}, 'kind', {kind}));", ct);

            // Multicaja event
            var evType = (reqQty.Values.Sum() == sale.Lines.Sum(l => l.Qty) && total == sale.Total) ? "Sale.Refunded" : "Sale.PartiallyRefunded";
            var ev = new EventLog
            {
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                At = DateTimeOffset.UtcNow,
                Type = evType,
                EntityType = "Refund",
                EntityId = refund.RefundId,
                TerminalId = cmd.TerminalId,
                RequestId = cmd.RequestId,
                DataJson = JsonSerializer.Serialize(new { saleId = sale.SaleId, refundId = refund.RefundId, total, kind })
            };
            _db.EventLog.Add(ev);

            _db.Outbox.Add(new OutboxItem
            {
                OutboxId = Guid.NewGuid(),
                TenantId = cmd.TenantId,
                BranchId = cmd.BranchId,
                CreatedAt = DateTimeOffset.UtcNow,
                Topic = "multicaja.events",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    type = evType,
                    tenantId = cmd.TenantId,
                    branchId = cmd.BranchId,
                    seq = 0,
                    at = ev.At,
                    data = new { saleId = sale.SaleId, refundId = refund.RefundId, total, kind }
                }),
                Status = "pending",
                Attempts = 0
            });

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(true, "OK", null, refund.RefundId, j.JournalId, ev.Seq);
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync(ct);
            return new(false, "DB_ERROR", ex.Message, null, null, null);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return new(false, "ERROR", ex.Message, null, null, null);
        }
    }
}

