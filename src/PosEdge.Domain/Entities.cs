namespace PosEdge.Domain;

public sealed class Tenant
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class Branch
{
    public Guid BranchId { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string Timezone { get; set; } = "America/Mexico_City";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class Counter
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public string Key { get; set; } = "";
    public long Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Terminal
{
    public Guid TerminalId { get; set; }
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public string Name { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string Status { get; set; } = "active";
    public string? CertThumbprint { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class Product
{
    public Guid ProductId { get; set; }
    public Guid TenantId { get; set; }
    public string Sku { get; set; } = "";
    public string? Barcode { get; set; }
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "ea";
    public decimal TaxRate { get; set; }
    public decimal Price { get; set; }
    public decimal Cost { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class Inventory
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid ProductId { get; set; }
    public decimal OnHand { get; set; }
    public decimal Reserved { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CashSession
{
    public Guid CashSessionId { get; set; }
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid TerminalId { get; set; }
    public Guid OpenedBy { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public Guid? ClosedBy { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public decimal OpeningAmount { get; set; }
    public string Status { get; set; } = "open";

    public string? OpenRequestId { get; set; }
    public string? CloseRequestId { get; set; }
    public decimal? ExpectedAmount { get; set; }
    public decimal? CountedAmount { get; set; }
    public decimal? DiscrepancyAmount { get; set; }
    public string? ReconciliationStatus { get; set; }
    public Guid? LastCashCountId { get; set; }
    public string? Note { get; set; }
}

public sealed class Sale
{
    public Guid SaleId { get; set; }
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid TerminalId { get; set; }
    public Guid CashSessionId { get; set; }
    public string RequestId { get; set; } = "";
    public long ServerTicket { get; set; }
    public string Status { get; set; } = "committed";
    public string Currency { get; set; } = "MXN";
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public string? VoidReason { get; set; }

    public List<SaleLine> Lines { get; set; } = new();
    public List<Payment> Payments { get; set; } = new();
}

public sealed class FinancialJournal
{
    public Guid JournalId { get; set; }
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public string RequestId { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string Kind { get; set; } = "";
    public string RefType { get; set; } = "";
    public Guid? RefId { get; set; }
    public Guid? TerminalId { get; set; }
    public Guid? CashSessionId { get; set; }
    public string Currency { get; set; } = "MXN";
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public string MetaJson { get; set; } = "{}";

    public List<FinancialJournalLine> Lines { get; set; } = new();
}

public sealed class FinancialJournalLine
{
    public Guid JournalId { get; set; }
    public int LineNo { get; set; }
    public string Account { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string? Note { get; set; }
}

public sealed class AuditLog
{
    public Guid AuditId { get; set; }
    public DateTimeOffset At { get; set; }
    public string ActorType { get; set; } = "";
    public Guid? ActorId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? BranchId { get; set; }
    public string? RequestId { get; set; }
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public string Classification { get; set; } = "ops";
    public string PayloadJson { get; set; } = "{}";
}

public sealed class SaleLine
{
    public Guid SaleLineId { get; set; }
    public Guid SaleId { get; set; }
    public Guid ProductId { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TaxRate { get; set; }
    public decimal LineSubtotal { get; set; }
    public decimal LineTax { get; set; }
    public decimal LineTotal { get; set; }
}

public sealed class Payment
{
    public Guid PaymentId { get; set; }
    public Guid SaleId { get; set; }
    public string Method { get; set; } = "cash";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "MXN";
    public string? MetaJson { get; set; }
}

public sealed class Refund
{
    public Guid RefundId { get; set; }
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid SaleId { get; set; }
    public string RequestId { get; set; } = "";
    public Guid? TerminalId { get; set; }
    public Guid? CashSessionId { get; set; }
    public string Kind { get; set; } = "refund";   // refund|credit_note
    public string Status { get; set; } = "posted";
    public DateTimeOffset CreatedAt { get; set; }
    public string Currency { get; set; } = "MXN";
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public string? Note { get; set; }

    public List<RefundLine> Lines { get; set; } = new();
    public List<RefundPayment> Payments { get; set; } = new();
}

public sealed class RefundLine
{
    public Guid RefundId { get; set; }
    public Guid SaleLineId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TaxRate { get; set; }
    public decimal LineSubtotal { get; set; }
    public decimal LineTax { get; set; }
    public decimal LineTotal { get; set; }
}

public sealed class RefundPayment
{
    public Guid RefundPaymentId { get; set; }
    public Guid RefundId { get; set; }
    public string Method { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "MXN";
    public string? MetaJson { get; set; }
}

public sealed class EventLog
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public long Seq { get; set; }
    public DateTimeOffset At { get; set; }
    public string Type { get; set; } = "";
    public string EntityType { get; set; } = "";
    public Guid? EntityId { get; set; }
    public Guid? TerminalId { get; set; }
    public string? RequestId { get; set; }
    public string DataJson { get; set; } = "{}";
}

public sealed class OutboxItem
{
    public Guid OutboxId { get; set; }
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Topic { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
}

public sealed class TerminalSyncState
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid TerminalId { get; set; }
    public long LastAppliedSeq { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class InventoryLease
{
    public Guid LeaseId { get; set; }
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid TerminalId { get; set; }
    public string RequestId { get; set; } = "";
    public string Status { get; set; } = "active"; // active|expired|revoked
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RenewedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? Note { get; set; }

    public List<InventoryLeaseLine> Lines { get; set; } = new();
}

public sealed class InventoryLeaseLine
{
    public Guid LeaseId { get; set; }
    public Guid ProductId { get; set; }
    public decimal QtyAllocated { get; set; }
    public decimal QtyUsed { get; set; }
}

