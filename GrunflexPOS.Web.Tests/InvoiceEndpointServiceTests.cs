using GrunflexPOS.Web.Data;
using GrunflexPOS.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GrunflexPOS.Web.Tests;

public sealed class InvoiceEndpointServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"grunflex-inv-probe-{Guid.NewGuid():N}.db");
    private readonly LocalPosStore _store;

    public InvoiceEndpointServiceTests()
    {
        var options = new DbContextOptionsBuilder<LocalPosDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=false")
            .Options;
        _store = TestConfiguration.CreateStore(options);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://invalid.test")]
    [InlineData("not-a-url")]
    public async Task ProbeAsync_RejectsInvalidEndpoint(string endpoint)
    {
        await _store.EnsureCreatedAsync();
        var service = CreateService();
        var (ok, _) = await service.ProbeAsync(endpoint);
        Assert.False(ok);
    }

    [Fact]
    public async Task ProbeAsync_AcceptsReachableHttpEndpoint()
    {
        await _store.EnsureCreatedAsync();
        var service = CreateService();
        var (ok, message) = await service.ProbeAsync("https://example.com");
        Assert.True(ok);
        Assert.Contains("200", message);
    }

    private InvoiceEmissionService CreateService()
    {
        var provider = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        return new InvoiceEmissionService(
            _store,
            provider.GetRequiredService<IHttpClientFactory>(),
            NullLogger<InvoiceEmissionService>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_databasePath); } catch { /* best effort */ }
    }
}
