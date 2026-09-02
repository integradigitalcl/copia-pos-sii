using GrunflexPOS.Web.Data;
using GrunflexPOS.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GrunflexPOS.Web.Tests;

public sealed class LocalReportsServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"grunflex-reports-test-{Guid.NewGuid():N}.db");
    private readonly TestDbFactory _factory;
    private readonly LocalPosStore _store;
    private readonly LocalReportsService _reports;

    public LocalReportsServiceTests()
    {
        var options = new DbContextOptionsBuilder<LocalPosDbContext>()
            .UseSqlite($"Data Source={_databasePath}").Options;
        _factory = new TestDbFactory(options);
        _store = TestConfiguration.CreateStore(options);
        _reports = new LocalReportsService(_factory);
    }

    [Fact]
    public async Task DashboardUsesLocalDayBoundariesAndExcludesCancelledSales()
    {
        await _store.EnsureCreatedAsync();
        var product = (await _store.GetProductsAsync()).First();
        var first = await _store.RecordSaleAsync([new CartItem(product, 1)], "ana", "Efectivo",
            printTicket: false);
        var second = await _store.RecordSaleAsync([new CartItem(product, 1)], "ana", "Tarjeta",
            printTicket: false);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var today = DateTime.Today;
            db.Sales.Single(x => x.Id == first.SaleId).CreatedAtUtc =
                DateTime.SpecifyKind(today.AddMinutes(10), DateTimeKind.Local).ToUniversalTime();
            db.Sales.Single(x => x.Id == second.SaleId).CreatedAtUtc =
                DateTime.SpecifyKind(today.AddDays(-1).AddHours(23.9), DateTimeKind.Local).ToUniversalTime();
            await db.SaveChangesAsync();
        }

        var report = await _reports.GetDashboardAsync(
            DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified),
            DateTime.SpecifyKind(DateTime.Today.AddDays(1), DateTimeKind.Unspecified));

        Assert.Equal(1, report.Transactions);
        Assert.Equal(product.Price, report.Total);
        Assert.Single(report.Payments);
        Assert.Equal("Efectivo", report.Payments[0].Method);
    }

    [Fact]
    public async Task SalesFiltersByCashierAndCreditAndReturnsDetails()
    {
        await _store.EnsureCreatedAsync();
        var product = (await _store.GetProductsAsync()).First();
        await _store.RecordSaleAsync([new CartItem(product, 1)], "ana", "Crédito", printTicket: false);
        await _store.RecordSaleAsync([new CartItem(product, 1)], "bruno", "Efectivo", printTicket: false);

        var today = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified);
        var page = await _reports.GetSalesAsync(today, today.AddDays(1), null, "ana", true, 1, 20);

        Assert.Single(page.Rows);
        Assert.Equal("ana", page.Rows[0].UserName);
        Assert.Equal("Crédito", page.Rows[0].PaymentMethod);
        var detail = await _reports.GetSaleDetailAsync(page.Rows[0].Id);
        Assert.NotNull(detail);
        Assert.Single(detail!.Lines);
    }

    [Fact]
    public async Task CancellingSaleRestoresStockAndMarksTicket()
    {
        await _store.EnsureCreatedAsync();
        var product = (await _store.GetProductsAsync()).First();
        var before = product.Stock;
        var sale = await _store.RecordSaleAsync([new CartItem(product, 2)], "ana", "Efectivo",
            printTicket: false);
        var result = await _reports.CancelSaleAsync(sale.SaleId!.Value, "supervisor");

        Assert.True(result.Success);
        Assert.Equal(before, (await _store.GetProductsAsync()).Single(x => x.Id == product.Id).Stock);
        Assert.True((await _reports.GetSaleDetailAsync(sale.SaleId.Value))!.Cancelled);
    }

    [Fact]
    public async Task RefundingOneLineUnitRestoresOnlyThatQuantityAndCash()
    {
        await _store.EnsureCreatedAsync();
        var product = (await _store.GetProductsAsync()).First();
        var before = product.Stock;
        var sale = await _store.RecordSaleAsync([new CartItem(product, 3)], "ana", "Efectivo",
            printTicket: false);
        var detail = await _reports.GetSaleDetailAsync(sale.SaleId!.Value);
        var result = await _reports.RefundLineAsync(sale.SaleId.Value, detail!.Lines[0].Id, 1, "supervisor");

        Assert.True(result.Success);
        Assert.Equal(before - 2, (await _store.GetProductsAsync()).Single(x => x.Id == product.Id).Stock);
        var refreshed = await _reports.GetSaleDetailAsync(sale.SaleId.Value);
        Assert.Equal(2m, refreshed!.Lines[0].Quantity);
        Assert.Equal(product.Price * 2, refreshed.Total);
    }

    [Fact]
    public async Task DashboardIncludesPersonalConsumptionLines()
    {
        await _store.EnsureCreatedAsync();
        var product = (await _store.GetProductsAsync()).First();
        await _store.RecordSaleAsync([new CartItem(product, 2)], "ana", "Efectivo",
            personalConsumption: true, printTicket: false);

        var today = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified);
        var report = await _reports.GetDashboardAsync(today, today.AddDays(1));

        Assert.Single(report.PersonalConsumption);
        Assert.Equal("ana", report.PersonalConsumption[0].Cashier);
        Assert.Equal(2m, report.PersonalConsumption[0].Quantity);
        Assert.Equal(product.Price * 2, report.PersonalConsumption[0].Total);
        Assert.Equal(0, report.Transactions);
    }

    [Fact]
    public async Task EditingSaleReducesLinesRestoresStockAndRecordsAudit()
    {
        await _store.EnsureCreatedAsync();
        var product = (await _store.GetProductsAsync()).First();
        var before = product.Stock;
        var sale = await _store.RecordSaleAsync([new CartItem(product, 3)], "ana", "Efectivo",
            printTicket: false);
        var detail = await _reports.GetSaleDetailAsync(sale.SaleId!.Value);

        var result = await _reports.EditSaleAsync(
            sale.SaleId.Value,
            [new EditSaleLineRequest(detail!.Lines[0].Id, 1)],
            "supervisor");

        Assert.True(result.Success);
        Assert.Equal(before - 1, (await _store.GetProductsAsync()).Single(x => x.Id == product.Id).Stock);
        var refreshed = await _reports.GetSaleDetailAsync(sale.SaleId.Value);
        Assert.True(refreshed!.Edited);
        Assert.Equal("supervisor", refreshed.EditedByUserName);
        Assert.Single(refreshed.Edits);
        Assert.Equal(1m, refreshed.Lines[0].Quantity);
        Assert.Equal(product.Price, refreshed.Total);
    }

    [Fact]
    public async Task EditingDiscountedSaleRecalculatesTotalsAndCashSession()
    {
        await _store.EnsureCreatedAsync();
        var product = (await _store.GetProductsAsync()).First();
        var sale = await _store.RecordSaleAsync(
            [new CartItem(product, 2, 10)], "ana", "Efectivo", printTicket: false);
        var detail = await _reports.GetSaleDetailAsync(sale.SaleId!.Value);
        var expectedTotal = Math.Round(product.Price * 2 * 0.9m, 2, MidpointRounding.AwayFromZero);
        var expectedDiscount = Math.Round(product.Price * 2 - expectedTotal, 2, MidpointRounding.AwayFromZero);

        Assert.Equal(expectedTotal, detail!.Total);
        Assert.Equal(expectedDiscount, detail.Discount);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var sessionBefore = await db.CashSessions.SingleAsync(x => x.Open);
            Assert.Equal(expectedTotal, sessionBefore.TotalSales);
        }

        var result = await _reports.EditSaleAsync(
            sale.SaleId.Value,
            [new EditSaleLineRequest(detail.Lines[0].Id, 1)],
            "supervisor");

        Assert.True(result.Success);
        var refreshed = await _reports.GetSaleDetailAsync(sale.SaleId.Value);
        var newTotal = Math.Round(product.Price * 0.9m, 2, MidpointRounding.AwayFromZero);
        var newDiscount = Math.Round(product.Price - newTotal, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(1m, refreshed!.Lines[0].Quantity);
        Assert.Equal(product.Price, refreshed.Subtotal);
        Assert.Equal(newDiscount, refreshed.Discount);
        Assert.Equal(newTotal, refreshed.Total);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var sessionAfter = await db.CashSessions.SingleAsync(x => x.Open);
            Assert.Equal(newTotal, sessionAfter.TotalSales);
            var storedSale = await db.Sales.SingleAsync(x => x.Id == sale.SaleId.Value);
            Assert.Equal(newTotal, storedSale.CashSessionAmount);
            Assert.Equal(newTotal, storedSale.ReceivedAmount);
        }
    }

    [Fact]
    public async Task EditingSaleFromClosedSessionRegistersRefundOnOpenSession()
    {
        await _store.EnsureCreatedAsync();
        var product = (await _store.GetProductsAsync()).First();
        var sale = await _store.RecordSaleAsync([new CartItem(product, 2)], "ana", "Efectivo",
            printTicket: false);
        var detail = await _reports.GetSaleDetailAsync(sale.SaleId!.Value);
        var originalTotal = product.Price * 2;

        await _store.CloseCashSessionAsync(originalTotal);
        await _store.OpenCashSessionAsync("ana", 0);
        var refundAmount = product.Price;

        var result = await _reports.EditSaleAsync(
            sale.SaleId.Value,
            [new EditSaleLineRequest(detail!.Lines[0].Id, 1)],
            "supervisor");

        Assert.True(result.Success);
        await using var db = await _factory.CreateDbContextAsync();
        var openSession = await db.CashSessions.SingleAsync(x => x.Open);
        Assert.Equal(refundAmount, openSession.TotalExits);
        Assert.Equal(-refundAmount, openSession.OpeningAmount + openSession.TotalSales +
            openSession.TotalEntries - openSession.TotalExits);
    }

    public void Dispose()
    {
        try { File.Delete(_databasePath); } catch { }
    }

    private sealed class TestDbFactory(DbContextOptions<LocalPosDbContext> options)
        : IDbContextFactory<LocalPosDbContext>
    {
        public LocalPosDbContext CreateDbContext() => new(options);
        public Task<LocalPosDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new LocalPosDbContext(options));
    }
}
