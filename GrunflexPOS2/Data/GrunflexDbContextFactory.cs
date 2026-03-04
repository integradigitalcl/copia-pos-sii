using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GrunflexPOS2.Data
{
    public class GrunflexDbContextFactory : IDesignTimeDbContextFactory<GrunflexDbContext>
    {
        public GrunflexDbContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<GrunflexDbContext>();

            optionsBuilder.UseNpgsql(
                "Host=192.168.100.17;Port=5432;Database=grunflexpos2;Username=postgres;Password=2106");

            return new GrunflexDbContext(optionsBuilder.Options);
        }
    }
}