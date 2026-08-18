using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GrunflexPOS2.Data
{
    public class GrunflexDbContextFactory : IDesignTimeDbContextFactory<GrunflexDbContext>
    {
        public GrunflexDbContext CreateDbContext(string[] args)
        {
            LocalDatabasePaths.EnsureDataDirectoryExists();
            var dbPath = Path.Combine(LocalDatabasePaths.DataDirectory, "design_grunflex.db");
            var cs = $"Data Source={dbPath}";
            var options = new DbContextOptionsBuilder<GrunflexDbContext>()
                .UseSqlite(cs)
                .Options;

            return new GrunflexDbContext(options);
        }
    }
}
