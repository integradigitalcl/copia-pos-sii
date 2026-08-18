using Microsoft.EntityFrameworkCore;
using PosEdge.Domain;

namespace PosEdge.Infrastructure;

public sealed class PosEdgeDbContext : DbContext
{
    public PosEdgeDbContext(DbContextOptions<PosEdgeDbContext> options) : base(options) { }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Counter> Counters => Set<Counter>();
    public DbSet<Terminal> Terminals => Set<Terminal>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Inventory> Inventory => Set<Inventory>();
    public DbSet<CashSession> CashSessions => Set<CashSession>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleLine> SaleLines => Set<SaleLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<EventLog> EventLog => Set<EventLog>();
    public DbSet<OutboxItem> Outbox => Set<OutboxItem>();
    public DbSet<TerminalSyncState> TerminalSyncState => Set<TerminalSyncState>();
    public DbSet<InventoryLease> InventoryLeases => Set<InventoryLease>();
    public DbSet<InventoryLeaseLine> InventoryLeaseLines => Set<InventoryLeaseLine>();
    public DbSet<FinancialJournal> FinancialJournal => Set<FinancialJournal>();
    public DbSet<FinancialJournalLine> FinancialJournalLines => Set<FinancialJournalLine>();
    public DbSet<AuditLog> AuditLog => Set<AuditLog>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<RefundLine> RefundLines => Set<RefundLine>();
    public DbSet<RefundPayment> RefundPayments => Set<RefundPayment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(x => x.TenantId);
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        });

        b.Entity<Branch>(e =>
        {
            e.ToTable("branches");
            e.HasKey(x => x.BranchId);
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Timezone).HasColumnName("timezone");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        });

        b.Entity<Counter>(e =>
        {
            e.ToTable("counters");
            e.HasKey(x => new { x.TenantId, x.BranchId, x.Key });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.Key).HasColumnName("key");
            e.Property(x => x.Value).HasColumnName("value");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        b.Entity<Terminal>(e =>
        {
            e.ToTable("terminals");
            e.HasKey(x => x.TerminalId);
            e.Property(x => x.TerminalId).HasColumnName("terminal_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.MachineName).HasColumnName("machine_name");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CertThumbprint).HasColumnName("cert_thumbprint");
            e.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        });

        b.Entity<Product>(e =>
        {
            e.ToTable("products");
            e.HasKey(x => x.ProductId);
            e.Property(x => x.ProductId).HasColumnName("product_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Sku).HasColumnName("sku");
            e.Property(x => x.Barcode).HasColumnName("barcode");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Unit).HasColumnName("unit");
            e.Property(x => x.TaxRate).HasColumnName("tax_rate");
            e.Property(x => x.Price).HasColumnName("price");
            e.Property(x => x.Cost).HasColumnName("cost");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        });

        b.Entity<Inventory>(e =>
        {
            e.ToTable("inventory");
            e.HasKey(x => new { x.TenantId, x.BranchId, x.ProductId });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.ProductId).HasColumnName("product_id");
            e.Property(x => x.OnHand).HasColumnName("on_hand");
            e.Property(x => x.Reserved).HasColumnName("reserved");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        b.Entity<CashSession>(e =>
        {
            e.ToTable("cash_sessions");
            e.HasKey(x => x.CashSessionId);
            e.Property(x => x.CashSessionId).HasColumnName("cash_session_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.TerminalId).HasColumnName("terminal_id");
            e.Property(x => x.OpenedBy).HasColumnName("opened_by");
            e.Property(x => x.OpenedAt).HasColumnName("opened_at");
            e.Property(x => x.ClosedBy).HasColumnName("closed_by");
            e.Property(x => x.ClosedAt).HasColumnName("closed_at");
            e.Property(x => x.OpeningAmount).HasColumnName("opening_amount");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.OpenRequestId).HasColumnName("open_request_id");
            e.Property(x => x.CloseRequestId).HasColumnName("close_request_id");
            e.Property(x => x.ExpectedAmount).HasColumnName("expected_amount");
            e.Property(x => x.CountedAmount).HasColumnName("counted_amount");
            e.Property(x => x.DiscrepancyAmount).HasColumnName("discrepancy_amount");
            e.Property(x => x.ReconciliationStatus).HasColumnName("reconciliation_status");
            e.Property(x => x.LastCashCountId).HasColumnName("last_cash_count_id");
            e.Property(x => x.Note).HasColumnName("note");
        });

        b.Entity<Sale>(e =>
        {
            e.ToTable("sales");
            e.HasKey(x => x.SaleId);
            e.Property(x => x.SaleId).HasColumnName("sale_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.TerminalId).HasColumnName("terminal_id");
            e.Property(x => x.CashSessionId).HasColumnName("cash_session_id");
            e.Property(x => x.RequestId).HasColumnName("request_id");
            e.Property(x => x.ServerTicket).HasColumnName("server_ticket");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Currency).HasColumnName("currency");
            e.Property(x => x.Subtotal).HasColumnName("subtotal");
            e.Property(x => x.Tax).HasColumnName("tax");
            e.Property(x => x.Total).HasColumnName("total");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.VoidedAt).HasColumnName("voided_at");
            e.Property(x => x.VoidReason).HasColumnName("void_reason");
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.SaleId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Payments).WithOne().HasForeignKey(p => p.SaleId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SaleLine>(e =>
        {
            e.ToTable("sale_lines");
            e.HasKey(x => x.SaleLineId);
            e.Property(x => x.SaleLineId).HasColumnName("sale_line_id");
            e.Property(x => x.SaleId).HasColumnName("sale_id");
            e.Property(x => x.ProductId).HasColumnName("product_id");
            e.Property(x => x.Sku).HasColumnName("sku");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Qty).HasColumnName("qty");
            e.Property(x => x.UnitPrice).HasColumnName("unit_price");
            e.Property(x => x.TaxRate).HasColumnName("tax_rate");
            e.Property(x => x.LineSubtotal).HasColumnName("line_subtotal");
            e.Property(x => x.LineTax).HasColumnName("line_tax");
            e.Property(x => x.LineTotal).HasColumnName("line_total");
        });

        b.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.HasKey(x => x.PaymentId);
            e.Property(x => x.PaymentId).HasColumnName("payment_id");
            e.Property(x => x.SaleId).HasColumnName("sale_id");
            e.Property(x => x.Method).HasColumnName("method");
            e.Property(x => x.Amount).HasColumnName("amount");
            e.Property(x => x.Currency).HasColumnName("currency");
            e.Property(x => x.MetaJson).HasColumnName("meta").HasColumnType("jsonb");
        });

        b.Entity<EventLog>(e =>
        {
            e.ToTable("event_log");
            e.HasKey(x => new { x.TenantId, x.BranchId, x.Seq });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.Seq).HasColumnName("seq").ValueGeneratedOnAdd();
            e.Property(x => x.At).HasColumnName("at");
            e.Property(x => x.Type).HasColumnName("type");
            e.Property(x => x.EntityType).HasColumnName("entity_type");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.TerminalId).HasColumnName("terminal_id");
            e.Property(x => x.RequestId).HasColumnName("request_id");
            e.Property(x => x.DataJson).HasColumnName("data").HasColumnType("jsonb");
        });

        b.Entity<OutboxItem>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(x => x.OutboxId);
            e.Property(x => x.OutboxId).HasColumnName("outbox_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.Topic).HasColumnName("topic");
            e.Property(x => x.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Attempts).HasColumnName("attempts");
            e.Property(x => x.LastError).HasColumnName("last_error");
            e.Property(x => x.NextRetryAt).HasColumnName("next_retry_at");
        });

        b.Entity<TerminalSyncState>(e =>
        {
            e.ToTable("terminal_sync_state");
            e.HasKey(x => new { x.TenantId, x.BranchId, x.TerminalId });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.TerminalId).HasColumnName("terminal_id");
            e.Property(x => x.LastAppliedSeq).HasColumnName("last_applied_seq");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        b.Entity<InventoryLease>(e =>
        {
            e.ToTable("inventory_leases");
            e.HasKey(x => x.LeaseId);
            e.Property(x => x.LeaseId).HasColumnName("lease_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.TerminalId).HasColumnName("terminal_id");
            e.Property(x => x.RequestId).HasColumnName("request_id");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.RenewedAt).HasColumnName("renewed_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.Property(x => x.Note).HasColumnName("note");
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.LeaseId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<InventoryLeaseLine>(e =>
        {
            e.ToTable("inventory_lease_lines");
            e.HasKey(x => new { x.LeaseId, x.ProductId });
            e.Property(x => x.LeaseId).HasColumnName("lease_id");
            e.Property(x => x.ProductId).HasColumnName("product_id");
            e.Property(x => x.QtyAllocated).HasColumnName("qty_allocated");
            e.Property(x => x.QtyUsed).HasColumnName("qty_used");
        });

        b.Entity<FinancialJournal>(e =>
        {
            e.ToTable("financial_journal");
            e.HasKey(x => x.JournalId);
            e.Property(x => x.JournalId).HasColumnName("journal_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.RequestId).HasColumnName("request_id");
            e.Property(x => x.At).HasColumnName("at");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.RefType).HasColumnName("ref_type");
            e.Property(x => x.RefId).HasColumnName("ref_id");
            e.Property(x => x.TerminalId).HasColumnName("terminal_id");
            e.Property(x => x.CashSessionId).HasColumnName("cash_session_id");
            e.Property(x => x.Currency).HasColumnName("currency");
            e.Property(x => x.TotalDebit).HasColumnName("total_debit");
            e.Property(x => x.TotalCredit).HasColumnName("total_credit");
            e.Property(x => x.MetaJson).HasColumnName("meta").HasColumnType("jsonb");
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.JournalId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<FinancialJournalLine>(e =>
        {
            e.ToTable("financial_journal_lines");
            e.HasKey(x => new { x.JournalId, x.LineNo });
            e.Property(x => x.JournalId).HasColumnName("journal_id");
            e.Property(x => x.LineNo).HasColumnName("line_no");
            e.Property(x => x.Account).HasColumnName("account");
            e.Property(x => x.Debit).HasColumnName("debit");
            e.Property(x => x.Credit).HasColumnName("credit");
            e.Property(x => x.Note).HasColumnName("note");
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(x => x.AuditId);
            e.Property(x => x.AuditId).HasColumnName("audit_id");
            e.Property(x => x.At).HasColumnName("at");
            e.Property(x => x.ActorType).HasColumnName("actor_type");
            e.Property(x => x.ActorId).HasColumnName("actor_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.RequestId).HasColumnName("request_id");
            e.Property(x => x.Action).HasColumnName("action");
            e.Property(x => x.EntityType).HasColumnName("entity_type");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.Classification).HasColumnName("classification");
            e.Property(x => x.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
        });

        b.Entity<Refund>(e =>
        {
            e.ToTable("refunds");
            e.HasKey(x => x.RefundId);
            e.Property(x => x.RefundId).HasColumnName("refund_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.BranchId).HasColumnName("branch_id");
            e.Property(x => x.SaleId).HasColumnName("sale_id");
            e.Property(x => x.RequestId).HasColumnName("request_id");
            e.Property(x => x.TerminalId).HasColumnName("terminal_id");
            e.Property(x => x.CashSessionId).HasColumnName("cash_session_id");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.Currency).HasColumnName("currency");
            e.Property(x => x.Subtotal).HasColumnName("subtotal");
            e.Property(x => x.Tax).HasColumnName("tax");
            e.Property(x => x.Total).HasColumnName("total");
            e.Property(x => x.Note).HasColumnName("note");
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.RefundId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Payments).WithOne().HasForeignKey(p => p.RefundId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RefundLine>(e =>
        {
            e.ToTable("refund_lines");
            e.HasKey(x => new { x.RefundId, x.SaleLineId });
            e.Property(x => x.RefundId).HasColumnName("refund_id");
            e.Property(x => x.SaleLineId).HasColumnName("sale_line_id");
            e.Property(x => x.ProductId).HasColumnName("product_id");
            e.Property(x => x.Qty).HasColumnName("qty");
            e.Property(x => x.UnitPrice).HasColumnName("unit_price");
            e.Property(x => x.TaxRate).HasColumnName("tax_rate");
            e.Property(x => x.LineSubtotal).HasColumnName("line_subtotal");
            e.Property(x => x.LineTax).HasColumnName("line_tax");
            e.Property(x => x.LineTotal).HasColumnName("line_total");
        });

        b.Entity<RefundPayment>(e =>
        {
            e.ToTable("refund_payments");
            e.HasKey(x => x.RefundPaymentId);
            e.Property(x => x.RefundPaymentId).HasColumnName("refund_payment_id");
            e.Property(x => x.RefundId).HasColumnName("refund_id");
            e.Property(x => x.Method).HasColumnName("method");
            e.Property(x => x.Amount).HasColumnName("amount");
            e.Property(x => x.Currency).HasColumnName("currency");
            e.Property(x => x.MetaJson).HasColumnName("meta").HasColumnType("jsonb");
        });
    }
}

