using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.Web.Data;

public sealed class LocalPosDbContext(DbContextOptions<LocalPosDbContext> options) : DbContext(options)
{
    public DbSet<LocalProduct> Products => Set<LocalProduct>();
    public DbSet<LocalSale> Sales => Set<LocalSale>();
    public DbSet<LocalSaleLine> SaleLines => Set<LocalSaleLine>();
    public DbSet<LocalSaleEdit> SaleEdits => Set<LocalSaleEdit>();
    public DbSet<LocalCashSession> CashSessions => Set<LocalCashSession>();
    public DbSet<LocalCashMovement> CashMovements => Set<LocalCashMovement>();
    public DbSet<LocalInventoryMovement> InventoryMovements => Set<LocalInventoryMovement>();
    public DbSet<LocalSetting> Settings => Set<LocalSetting>();
    public DbSet<LocalUser> Users => Set<LocalUser>();
    public DbSet<LocalInvoiceEmission> InvoiceEmissions => Set<LocalInvoiceEmission>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LocalProduct>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<LocalSetting>().HasKey(x => x.Key);
        modelBuilder.Entity<LocalSaleLine>()
            .HasOne(x => x.Sale)
            .WithMany(x => x.Lines)
            .HasForeignKey(x => x.SaleId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LocalCashMovement>()
            .HasOne(x => x.Session)
            .WithMany(x => x.Movements)
            .HasForeignKey(x => x.CashSessionId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LocalSale>().HasIndex(x => x.TicketNumber).IsUnique();
        modelBuilder.Entity<LocalSaleEdit>()
            .HasOne(x => x.Sale)
            .WithMany()
            .HasForeignKey(x => x.SaleId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LocalInvoiceEmission>().HasIndex(x => x.TicketNumber);
        modelBuilder.Entity<LocalInvoiceEmission>().HasIndex(x => x.RequestId).IsUnique();
    }
}

public sealed class LocalProduct
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal Cost { get; set; }
    public decimal WholesalePrice { get; set; }
    public decimal Stock { get; set; }
    public decimal MinStock { get; set; }
    public decimal MaxStock { get; set; }
    public string Unit { get; set; } = "un.";
    public string SaleType { get; set; } = "Unidad";
    public string Department { get; set; } = "General";
    public string Accent { get; set; } = "#2563EB";
    public int? CentralProductId { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class LocalSale
{
    public long Id { get; set; }
    public long TicketNumber { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string UserName { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public string Customer { get; set; } = "Público en general";
    public decimal Subtotal { get; set; }
    public decimal Discount { get; set; }
    public decimal Total { get; set; }
    public decimal ReceivedAmount { get; set; }
    public decimal ChangeAmount { get; set; }
    public bool PersonalConsumption { get; set; }
    public bool Cancelled { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public DateTime? EditedAtUtc { get; set; }
    public string? EditedByUserName { get; set; }
    public int EditCount { get; set; }
    public bool PrintTicket { get; set; } = true;
    public long? CashSessionId { get; set; }
    public decimal CashSessionAmount { get; set; }
    public List<LocalSaleLine> Lines { get; set; } = [];
}

public sealed class LocalSaleLine
{
    public long Id { get; set; }
    public long SaleId { get; set; }
    public LocalSale Sale { get; set; } = null!;
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public decimal ListUnitPrice { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercentage { get; set; }
    public decimal Quantity { get; set; }
    public decimal Total { get; set; }
}

public sealed class LocalSaleEdit
{
    public long Id { get; set; }
    public long SaleId { get; set; }
    public LocalSale Sale { get; set; } = null!;
    public DateTime EditedAtUtc { get; set; } = DateTime.UtcNow;
    public string UserName { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

public sealed class LocalCashSession
{
    public long Id { get; set; }
    public string RegisterName { get; set; } = "Caja 1";
    public string UserName { get; set; } = string.Empty;
    public DateTime OpenedAtUtc { get; set; } = DateTime.UtcNow;
    public decimal OpeningAmount { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public decimal? ClosingAmount { get; set; }
    public decimal Difference { get; set; }
    public decimal TotalSales { get; set; }
    public decimal TotalEntries { get; set; }
    public decimal TotalExits { get; set; }
    public bool Open { get; set; } = true;
    public List<LocalCashMovement> Movements { get; set; } = [];
}

public sealed class LocalCashMovement
{
    public long Id { get; set; }
    public long CashSessionId { get; set; }
    public LocalCashSession Session { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string Type { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
}

public sealed class LocalInventoryMovement
{
    public long Id { get; set; }
    public int ProductId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal StockBefore { get; set; }
    public decimal StockAfter { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LocalSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LocalUser
{
    public int Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "Cajero";
    public string Permissions { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LocalInvoiceEmission
{
    public long Id { get; set; }
    public long TicketNumber { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public string DocumentType { get; set; } = "boleta";
    public string Status { get; set; } = string.Empty;
    public string ProviderDocumentId { get; set; } = string.Empty;
    public string ProviderFolio { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string ResponseJson { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
