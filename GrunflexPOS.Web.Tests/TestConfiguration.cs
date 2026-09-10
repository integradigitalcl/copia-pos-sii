using GrunflexPOS.Web.Data;
using GrunflexPOS.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

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

    /// <summary>Crea el esquema y un producto de prueba (el catálogo de producción ya no se siembra).</summary>
    public static async Task<PosProduct> EnsureSampleProductAsync(
        LocalPosStore store,
        string code = "TEST-001",
        string name = "Producto prueba",
        decimal price = 1000m,
        decimal stock = 50m)
    {
        await store.EnsureCreatedAsync();
        var existing = await store.GetProductsAsync();
        if (existing.Count > 0)
            return existing[0];

        var created = await store.CreateProductAsync(
            code, name, "Pruebas", price * 0.6m, price, price, stock, 0, 0,
            "un.", "Unidad", "General", "test");
        Assert.True(created.Success, created.Message);
        return (await store.GetProductsAsync()).Single(x => x.Id == created.ProductId);
    }

    private sealed class TestDbFactory(DbContextOptions<LocalPosDbContext> options)
        : IDbContextFactory<LocalPosDbContext>
    {
        public LocalPosDbContext CreateDbContext() => new(options);
        public Task<LocalPosDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalPosDbContext(options));
    }
}
