using GrunflexPOS.Web.Data;
using GrunflexPOS.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrunflexPOS.Web.Tests;

internal static class TestConfiguration
{
    public static IConfiguration DemoCredentials() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AllowDemoCredentials"] = "true"
            })
            .Build();

    public static IConfiguration ProductionCredentials() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AllowDemoCredentials"] = "false"
            })
            .Build();

    public static LocalPosStore CreateStore(
        DbContextOptions<LocalPosDbContext> options,
        IConfiguration? configuration = null) =>
        new(new TestDbFactory(options), configuration ?? DemoCredentials(), NullLogger<LocalPosStore>.Instance);

    private sealed class TestDbFactory(DbContextOptions<LocalPosDbContext> options)
        : IDbContextFactory<LocalPosDbContext>
    {
        public LocalPosDbContext CreateDbContext() => new(options);
        public Task<LocalPosDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalPosDbContext(options));
    }
}
