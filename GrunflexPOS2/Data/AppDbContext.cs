using Microsoft.EntityFrameworkCore;
using GrunflexPOS2.Models;

namespace GrunflexPOS2.Data
{
    public class AppDbContext : DbContext
    {
        public DbSet<User> Users { get; set; }
        public DbSet<Caja> Cajas { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            options.UseSqlite("Data Source=grunflex.db");
        }
    }
}