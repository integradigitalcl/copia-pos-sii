using Microsoft.EntityFrameworkCore;
using GrunflexPOS2.Models.Entities;

namespace GrunflexPOS2.Data
{
    public class GrunflexDbContext : DbContext
    {
        public GrunflexDbContext(DbContextOptions<GrunflexDbContext> options)
            : base(options)
        {
        }

        // 🔥 TABLAS MULTICAJA BASE
        public DbSet<Empresa> Empresas { get; set; }
        public DbSet<Caja> Cajas { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // 🔥 RELACIÓN EMPRESA → CAJAS
            modelBuilder.Entity<Caja>()
                .HasOne(c => c.Empresa)
                .WithMany(e => e.Cajas)
                .HasForeignKey(c => c.EmpresaId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}