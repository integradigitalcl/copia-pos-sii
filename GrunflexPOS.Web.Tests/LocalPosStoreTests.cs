using GrunflexPOS.Web.Data;
using GrunflexPOS.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GrunflexPOS.Web.Tests;

public sealed class LocalPosStoreTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"grunflex-pos-test-{Guid.NewGuid():N}.db");
    private readonly LocalPosStore _store;

    public LocalPosStoreTests()
    {
        var options = new DbContextOptionsBuilder<LocalPosDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;
        _store = TestConfiguration.CreateStore(options);
    }

    [Fact]
    public async Task SeedCreatesCatalogAndSaleDecrementsStock()
    {
        await _store.EnsureCreatedAsync();
        var product = (await _store.GetProductsAsync()).First();
        var originalStock = product.Stock;

        var result = await _store.RecordSaleAsync([new CartItem(product, 2)], "admin", "Efectivo");

        Assert.True(result.Success);
        Assert.Equal(product.Price * 2, result.Total);
        var refreshed = (await _store.GetProductsAsync()).Single(x => x.Id == product.Id);
        Assert.Equal(originalStock - 2, refreshed.Stock);
        Assert.Equal(1, (await _store.GetDashboardAsync()).SalesToday);
    }

    [Fact]
    public async Task SaleWithEmptyCartIsRejected()
    {
        await _store.EnsureCreatedAsync();
        var result = await _store.RecordSaleAsync([], "admin", "Tarjeta");
        Assert.False(result.Success);
    }

    [Fact]
    public async Task SalePersistsTicketDiscountAndChange()
    {
        await _store.EnsureCreatedAsync();
        var product = (await _store.GetProductsAsync()).First();
        var result = await _store.RecordSaleAsync(
            [new CartItem(product, 2, 10)], "admin", "Efectivo",
            receivedAmount: product.Price * 2, printTicket: false);

        Assert.True(result.Success);
        Assert.Equal(product.Price * 2 * .9m, result.Total);
        Assert.Equal(product.Price * 2 * .1m, result.Change);
        var sale = (await _store.GetRecentSalesAsync()).First();
        Assert.Equal(result.TicketNumber, sale.TicketNumber);
        Assert.Equal(product.Price * 2 * .1m, sale.Discount);
    }

    [Fact]
    public async Task PersonalConsumptionDeductsStockWithoutRevenue()
    {
        await _store.EnsureCreatedAsync();
        var product = (await _store.GetProductsAsync()).First();
        var result = await _store.RecordSaleAsync(
            [new CartItem(product, 1)], "admin", "Consumo personal",
            personalConsumption: true, printTicket: false);

        Assert.True(result.Success);
        Assert.Equal(0m, result.Total);
        Assert.Equal(0, (await _store.GetDashboardAsync()).SalesToday);
        Assert.Equal(product.Stock - 1, (await _store.GetProductsAsync()).Single(x => x.Id == product.Id).Stock);
    }

    public void Dispose()
    {
        try { File.Delete(_databasePath); } catch { /* test cleanup is best effort */ }
    }

    private sealed class TestDbFactory(DbContextOptions<LocalPosDbContext> options) : IDbContextFactory<LocalPosDbContext>
    {
        public LocalPosDbContext CreateDbContext() => new(options);
        public Task<LocalPosDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalPosDbContext(options));
    }
}
