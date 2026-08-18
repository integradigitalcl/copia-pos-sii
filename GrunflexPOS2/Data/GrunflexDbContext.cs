using System.Threading;
using System.Threading.Tasks;
using GrunflexPOS2.Models;
using GrunflexPOS2.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS2.Data
{
    public class GrunflexDbContext : DbContext
    {
        public GrunflexDbContext(DbContextOptions<GrunflexDbContext> options)
            : base(options)
        {
        }

        public GrunflexDbContext()
        {
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                var config = AppConfig.Cargar();
                optionsBuilder.UseSqlite(config.ConnectionString);
            }
        }

        // 🔥 TABLAS PRINCIPALES
        public DbSet<Empresa> Empresas { get; set; }
        public DbSet<Caja> Cajas { get; set; }

        // 🔥 🔥 CLAVE: USUARIOS CORRECTOS
        public DbSet<Usuario> Usuarios { get; set; }

        public DbSet<VentaEntity> Ventas { get; set; }
        public DbSet<DetalleVenta> DetalleVentas { get; set; }

        public DbSet<CajaSesion> CajaSesiones { get; set; }
        public DbSet<MovimientoCaja> MovimientosCaja { get; set; }

        public DbSet<Producto> Productos { get; set; }
        public DbSet<Categoria> Categorias { get; set; }

        public DbSet<Configuracion> Configuraciones { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // 🔹 EMPRESA → CAJAS
            modelBuilder.Entity<Caja>()
                .HasOne(c => c.Empresa)
                .WithMany(e => e.Cajas)
                .HasForeignKey(c => c.EmpresaId)
                .OnDelete(DeleteBehavior.Cascade);

            // 🔹 VENTA → DETALLE
            modelBuilder.Entity<DetalleVenta>()
                .HasOne(d => d.Venta)
                .WithMany(v => v.Items)
                .HasForeignKey(d => d.VentaId)
                .OnDelete(DeleteBehavior.Cascade);

            // 🔹 CAJA → SESIONES
            modelBuilder.Entity<CajaSesion>()
                .HasOne<Caja>()
                .WithMany()
                .HasForeignKey(c => c.CajaId)
                .OnDelete(DeleteBehavior.Cascade);

            // 🔹 SESION → MOVIMIENTOS
            modelBuilder.Entity<MovimientoCaja>()
                .HasOne<CajaSesion>()
                .WithMany()
                .HasForeignKey(m => m.CajaSesionId)
                .OnDelete(DeleteBehavior.Cascade);

            // 🔥 🔥 USUARIO (ÍNDICE ÚNICO)
            modelBuilder.Entity<Usuario>()
                .HasIndex(u => u.Username)
                .IsUnique();

            // 🔹 PRODUCTO → CÓDIGO ÚNICO
            modelBuilder.Entity<Producto>()
                .HasIndex(p => p.CodigoBarras)
                .IsUnique();

            // 🔹 PRODUCTO → CATEGORÍA
            modelBuilder.Entity<Producto>()
                .HasOne(p => p.Categoria)
                .WithMany(c => c.Productos)
                .HasForeignKey(p => p.CategoriaId)
                .OnDelete(DeleteBehavior.SetNull);
        }

        public override int SaveChanges()
        {
            MulticajaLocalWriteGuard.ValidateAndThrow(this);
            return base.SaveChanges();
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            MulticajaLocalWriteGuard.ValidateAndThrow(this);
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            MulticajaLocalWriteGuard.ValidateAndThrow(this);
            return base.SaveChangesAsync(cancellationToken);
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
            CancellationToken cancellationToken = default)
        {
            MulticajaLocalWriteGuard.ValidateAndThrow(this);
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
    }
}