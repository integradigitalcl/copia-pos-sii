using GrunflexPOS.API.Commerce;
using GrunflexPOS.API.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GrunflexPOS.API.Data;

/// <summary>
/// Contexto EF apuntando exclusivamente a <c>grunflex.db</c> del POS (ventas, stock, cajas).
/// No mezclar con <see cref="ApiDbContext"/> (JWT / licencias en <c>grunflex_api.db</c>).
/// </summary>
public sealed class PosCommerceDbContext : DbContext
{
    public PosCommerceDbContext(DbContextOptions<PosCommerceDbContext> options)
        : base(options)
    {
    }

    public DbSet<CommerceEmpresa> Empresas => Set<CommerceEmpresa>();
    public DbSet<CommerceCaja> Cajas => Set<CommerceCaja>();
    public DbSet<CommerceUsuario> Usuarios => Set<CommerceUsuario>();
    public DbSet<CommerceCajaSesion> CajaSesiones => Set<CommerceCajaSesion>();
    public DbSet<CommerceMovimientoCaja> MovimientosCaja => Set<CommerceMovimientoCaja>();
    public DbSet<CommerceVenta> Ventas => Set<CommerceVenta>();
    public DbSet<CommerceDetalleVenta> DetalleVentas => Set<CommerceDetalleVenta>();
    public DbSet<CommerceProducto> Productos => Set<CommerceProducto>();
    public DbSet<InventoryStock> InventoryStocks => Set<InventoryStock>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<InventoryReservation> InventoryReservations => Set<InventoryReservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CommerceEmpresa>(e =>
        {
            e.ToTable("Empresas");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<CommerceCaja>(e =>
        {
            e.ToTable("Cajas");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.EmpresaId);
        });

        modelBuilder.Entity<CommerceUsuario>(e =>
        {
            e.ToTable("Usuarios");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Username).IsUnique();
        });

        modelBuilder.Entity<CommerceCajaSesion>(e =>
        {
            e.ToTable("CajaSesiones");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CajaId);
        });

        modelBuilder.Entity<CommerceMovimientoCaja>(e =>
        {
            e.ToTable("MovimientosCaja");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CajaSesionId);
            e.HasOne<CommerceCajaSesion>()
                .WithMany()
                .HasForeignKey(x => x.CajaSesionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommerceVenta>(e =>
        {
            e.ToTable("Ventas");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<CommerceDetalleVenta>(e =>
        {
            e.ToTable("DetalleVentas");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.VentaId);
            e.HasOne<CommerceVenta>()
                .WithMany()
                .HasForeignKey(x => x.VentaId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommerceProducto>(e =>
        {
            e.ToTable("Productos");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CodigoBarras).IsUnique();
        });

        modelBuilder.Entity<InventoryStock>(e =>
        {
            e.ToTable("InventoryStocks");
            e.HasKey(x => x.ProductId);
            e.Property(x => x.AverageUnitCost).HasPrecision(18, 4);
        });

        modelBuilder.Entity<InventoryMovement>(e =>
        {
            e.ToTable("InventoryMovements");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ProductId);
            e.HasIndex(x => x.CreatedAtUtc);
            e.Property(x => x.UnitCost).HasPrecision(18, 4);
            e.Property(x => x.TotalCost).HasPrecision(18, 4);
            e.Property(x => x.AverageUnitCostAfter).HasPrecision(18, 4);
        });

        modelBuilder.Entity<InventoryReservation>(e =>
        {
            e.ToTable("InventoryReservations");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ProductId);
        });

        ConfigureSqliteGuidTextStorage(modelBuilder);
    }

    /// <summary>
    /// SQLite almacena GUID como TEXT; filas del cliente WPF usan mayúsculas (compatibilidad legacy).
    /// </summary>
    private static void ConfigureSqliteGuidTextStorage(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(Guid))
                {
                    property.SetValueConverter(new ValueConverter<Guid, string>(
                        g => g.ToString("D").ToUpperInvariant(),
                        s => Guid.Parse(s)));
                }
                else if (property.ClrType == typeof(Guid?))
                {
                    property.SetValueConverter(new ValueConverter<Guid?, string>(
                        g => g.HasValue ? g.Value.ToString("D").ToUpperInvariant() : null!,
                        s => string.IsNullOrEmpty(s) ? null : Guid.Parse(s)));
                }
            }
        }
    }
}
