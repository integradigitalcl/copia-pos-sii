using GrunflexPOS.API.Models;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Data;

public class ApiDbContext : DbContext
{
    public ApiDbContext(DbContextOptions<ApiDbContext> options)
        : base(options)
    {
    }

    public DbSet<RefreshTokenEntity> RefreshTokens => Set<RefreshTokenEntity>();
    public DbSet<LicenseIssuerRecord> LicenseIssuerRecords => Set<LicenseIssuerRecord>();
    public DbSet<IssuerClientRecord> IssuerClientRecords => Set<IssuerClientRecord>();
    public DbSet<IssuerActivationRecord> IssuerActivationRecords => Set<IssuerActivationRecord>();
    public DbSet<StoredBackup> StoredBackups => Set<StoredBackup>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<TerminalRegistration> TerminalRegistrations => Set<TerminalRegistration>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<TerminalAuditLog> TerminalAuditLogs => Set<TerminalAuditLog>();
    public DbSet<TerminalHeartbeatRecord> TerminalHeartbeats => Set<TerminalHeartbeatRecord>();
    public DbSet<MulticajaSyncChangeLog> MulticajaSyncChangeLogs => Set<MulticajaSyncChangeLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RefreshTokenEntity>(e =>
        {
            e.ToTable("RefreshTokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.Username).HasMaxLength(120).IsRequired();
            e.Property(x => x.Role).HasMaxLength(50).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.Username, x.ExpiresAtUtc });
        });

        modelBuilder.Entity<LicenseIssuerRecord>(e =>
        {
            e.ToTable("LicenseIssuerRecords");
            e.HasKey(x => x.Id);
            e.Property(x => x.ActivationId).HasMaxLength(80).IsRequired();
            e.Property(x => x.CustomerName).HasMaxLength(150).IsRequired();
            e.Property(x => x.BusinessName).HasMaxLength(150).IsRequired();
            e.Property(x => x.LicenseType).HasMaxLength(40).IsRequired();
            e.Property(x => x.LicenseToken).HasMaxLength(4096).IsRequired();
            e.HasIndex(x => x.ActivationId);
            e.HasIndex(x => x.CreatedAtUtc);
        });

        modelBuilder.Entity<IssuerClientRecord>(e =>
        {
            e.ToTable("IssuerClientRecords");
            e.HasKey(x => x.Id);
            e.Property(x => x.ClientCode).HasMaxLength(32).IsRequired();
            e.Property(x => x.Name).HasMaxLength(150).IsRequired();
            e.Property(x => x.Business).HasMaxLength(150).IsRequired();
            e.Property(x => x.Email).HasMaxLength(200).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(40).IsRequired();
            e.HasIndex(x => x.ClientCode).IsUnique();
        });

        modelBuilder.Entity<IssuerActivationRecord>(e =>
        {
            e.ToTable("IssuerActivationRecords");
            e.HasKey(x => x.Id);
            e.Property(x => x.ActivationId).HasMaxLength(80).IsRequired();
            e.Property(x => x.CustomerDisplay).HasMaxLength(300).IsRequired();
            e.Property(x => x.DeviceName).HasMaxLength(120).IsRequired();
            e.Property(x => x.HardwareId).HasMaxLength(120).IsRequired();
            e.Property(x => x.Status).HasMaxLength(40).IsRequired();
            e.HasIndex(x => x.ActivationId);
            e.HasIndex(x => x.LastSeenUtc);
        });

        modelBuilder.Entity<StoredBackup>(e =>
        {
            e.ToTable("StoredBackups");
            e.HasKey(x => x.Id);
            e.Property(x => x.ActivationId).HasMaxLength(80).IsRequired();
            e.Property(x => x.OriginalFileName).HasMaxLength(260).IsRequired();
            e.Property(x => x.StorageFileName).HasMaxLength(260).IsRequired();
            e.Property(x => x.Sha256Hex).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.ActivationId);
            e.HasIndex(x => x.CreatedAtUtc);
        });

        modelBuilder.Entity<SupportTicket>(e =>
        {
            e.ToTable("SupportTickets");
            e.HasKey(x => x.Id);
            e.Property(x => x.ActivationId).HasMaxLength(80).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(200).IsRequired();
            e.Property(x => x.Body).HasMaxLength(8000).IsRequired();
            e.Property(x => x.Status).HasMaxLength(40).IsRequired();
            e.HasIndex(x => x.ActivationId);
            e.HasIndex(x => x.CreatedAtUtc);
        });

        modelBuilder.Entity<TerminalRegistration>(e =>
        {
            e.ToTable("TerminalRegistrations");
            e.HasKey(x => x.Id);
            e.Property(x => x.MachineFingerprint).HasMaxLength(120).IsRequired();
            e.Property(x => x.MachineName).HasMaxLength(120).IsRequired();
            e.Property(x => x.Version).HasMaxLength(40).IsRequired();
            e.Property(x => x.ActivationId).HasMaxLength(80).IsRequired();
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.TerminalTokenHash).HasMaxLength(64);
            e.Property(x => x.DisplayName).HasMaxLength(120);
            e.Property(x => x.DeactivatedReason).HasMaxLength(500);
            e.HasIndex(x => x.MachineFingerprint).IsUnique();
            e.HasIndex(x => x.InstallationId).IsUnique();
            e.HasIndex(x => x.LastHeartbeatUtc);
            e.HasIndex(x => x.Active);
            e.HasIndex(x => x.CajaId);
        });

        modelBuilder.Entity<TerminalAuditLog>(e =>
        {
            e.ToTable("TerminalAuditLogs");
            e.HasKey(x => x.Id);
            e.Property(x => x.EventType).HasMaxLength(64).IsRequired();
            e.Property(x => x.Details).HasMaxLength(4000);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.HasIndex(x => x.TerminalId);
            e.HasIndex(x => x.CreatedAtUtc);
        });

        modelBuilder.Entity<TerminalHeartbeatRecord>(e =>
        {
            e.ToTable("TerminalHeartbeats");
            e.HasKey(x => x.Id);
            e.Property(x => x.ClientVersion).HasMaxLength(40);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.HasIndex(x => x.TerminalId).IsUnique();
            e.HasIndex(x => x.LastSeenAtUtc);
            e.HasIndex(x => x.CajaId);
        });

        modelBuilder.Entity<MulticajaSyncChangeLog>(e =>
        {
            e.ToTable("MulticajaSyncChangeLogs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Domain).HasMaxLength(32).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(64).IsRequired();
            e.Property(x => x.ChangeType).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.Id);
            e.HasIndex(x => x.ChangedAtUtc);
            e.HasIndex(x => new { x.Domain, x.EntityId });
        });

        modelBuilder.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("IdempotencyRecords");
            e.HasKey(x => x.Id);
            e.Property(x => x.RequestId).HasMaxLength(64).IsRequired();
            e.Property(x => x.OperationType).HasMaxLength(64).IsRequired();
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.TerminalId).HasMaxLength(120).IsRequired();
            e.Property(x => x.ResponsePayload);
            e.Property(x => x.ResourceType).HasMaxLength(64);
            e.Property(x => x.ResourceId).HasMaxLength(64);
            e.HasIndex(x => new { x.RequestId, x.OperationType }).IsUnique();
            e.HasIndex(x => x.ExpiresAt);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CreatedAt);
        });
    }
}
