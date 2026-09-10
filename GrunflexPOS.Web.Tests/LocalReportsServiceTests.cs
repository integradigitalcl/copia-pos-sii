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
    public async Task DashboardComputesProfitMarginDepartmentsAndTax()
    {
        var product = await TestConfiguration.EnsureSampleProductAsync(_store);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var entity = db.Products.Single(x => x.Id == product.Id);
            entity.Cost = product.Price * 0.6m;
            entity.Department = "bebidas";
            await db.SaveChangesAsync();
        }

        product = (await _store.GetProductsAsync()).First(x => x.Id == product.Id);
        await _store.RecordSaleAsync([new CartItem(product, 2)], "ana", "Efectivo", printTicket: false);

        var today = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified);
        var report = await _reports.GetDashboardAsync(today, today.AddDays(1), 19m, pricesIncludeTax: true);

        Assert.Equal(1, report.Transactions);
        Assert.Equal(product.Price * 2, report.Total);
        Assert.Equal((product.Price - product.Cost) * 2, report.Profit);
        Assert.True(report.AvgMargin > 0);
        Assert.Contains(report.Departments, d => d.Department.Equals("bebidas", StringComparison.OrdinalIgnoreCase));
        Assert.True(report.TaxCollected > 0);
        Assert.True(report.TaxableSales > 0);
        Assert.Single(report.DaySeries);
    }

    [Fact]
    public async Task DashboardUsesLocalDayBoundariesAndExcludesCancelledSales()
    {
        var product = await TestConfiguration.EnsureSampleProductAsync(_store);
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
        var product = await TestConfiguration.EnsureSampleProductAsync(_store);
        await _store.RecordSaleAsync([new CartItem(product, 1)], "ana", "Transferencia", printTicket: false);
        await _store.RecordSaleAsync([new CartItem(product, 1)], "bruno", "Efectivo", printTicket: false);

        var today = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified);
        var page = await _reports.GetSalesAsync(today, today.AddDays(1), null, "ana", true, 1, 20);

        Assert.Single(page.Rows);
        Assert.Equal("ana", page.Rows[0].UserName);
        Assert.Equal("Transferencia", page.Rows[0].PaymentMethod);
        var detail = await _reports.GetSaleDetailAsync(page.Rows[0].Id);
        Assert.NotNull(detail);
        Assert.Single(detail!.Lines);
    }

    [Fact]
    public async Task CancellingSaleRestoresStockAndMarksTicket()
    {
        var product = await TestConfiguration.EnsureSampleProductAsync(_store);
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
        var product = await TestConfiguration.EnsureSampleProductAsync(_store);
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
        var product = await TestConfiguration.EnsureSampleProductAsync(_store);
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
        var product = await TestConfiguration.EnsureSampleProductAsync(_store);
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
        var product = await TestConfiguration.EnsureSampleProductAsync(_store);
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
        var product = await TestConfiguration.EnsureSampleProductAsync(_store);
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

    [Fact]
    public async Task CashCloseReportMatchesSessionTotalsAndFormatsTicket()
    {
        var product = await TestConfiguration.EnsureSampleProductAsync(_store);
        var sessionId = await _store.OpenCashSessionAsync("ana", 10000);
        await _store.RecordSaleAsync([new CartItem(product, 1)], "ana", "Efectivo",
            printTicket: false, cashSessionId: sessionId);
        await _store.RecordSaleAsync([new CartItem(product, 1)], "ana", "Tarjeta",
            printTicket: false, cashSessionId: sessionId);
        await _store.CloseCashSessionAsync(10000 + product.Price);

        var report = await _reports.GetCashCloseReportAsync(sessionId, 10000 + product.Price, 19m, true);
        Assert.NotNull(report);
        Assert.Equal(2, report!.Transactions);
        Assert.Equal(product.Price * 2, report.TotalSales);
        Assert.Equal(product.Price, report.CashPayments);
        Assert.Equal(product.Price, report.CardPayments);
        Assert.Equal(product.Price, report.CashFromSales);
        Assert.Equal(10000 + product.Price, report.ExpectedCash);
        Assert.Equal(0, report.Difference);
        Assert.StartsWith("CC-", report.Folio);

        var ticket = CashCloseTicketFormatter.Format(report, 42);
        Assert.Contains("COMPROBANTE DE CIERRE DE CAJA", ticket);
        Assert.Contains("CUADRATURA DE EFECTIVO", ticket);
        Assert.Contains("CONSUMO PERSONAL", ticket);
        Assert.Contains(report.Folio, ticket);

        var ticket58 = CashCloseTicketFormatter.Format(report, 32, 58);
        Assert.Contains("CIERRE DE CAJA", ticket58);
        Assert.Contains("CONSUMO PERS.", ticket58);
        Assert.True(ticket58.Split('\n').All(line => line.TrimEnd('\r').Length <= 32));
    }

    [Fact]
    public async Task CashCloseIncludesPersonalConsumptionWithoutCashImpact()
    {
        var product = await TestConfiguration.EnsureSampleProductAsync(_store);
        var sessionId = await _store.OpenCashSessionAsync("ana", 8000);
        var cashSale = await _store.RecordSaleAsync([new CartItem(product, 1)], "ana", "Efectivo",
            printTicket: false, cashSessionId: sessionId);
        Assert.True(cashSale.Success, cashSale.Message);
        var personal = await _store.RecordSaleAsync([new CartItem(product, 2)], "ana", "Efectivo",
            personalConsumption: true, printTicket: false, cashSessionId: sessionId);
        Assert.True(personal.Success, personal.Message);
        Assert.Equal(product.Price * 2, personal.Total);

        var expected = 8000 + product.Price;
        await _store.CloseCashSessionAsync(expected);

        var report = await _reports.GetCashCloseReportAsync(sessionId, expected, 19m, true);
        Assert.NotNull(report);
        Assert.Equal(1, report!.Transactions);
        Assert.Equal(product.Price, report.TotalSales);
        Assert.Equal(1, report.PersonalConsumptionCount);
        Assert.Equal(product.Price * 2, report.PersonalConsumptionTotal);
        Assert.Equal(expected, report.ExpectedCash);
        Assert.Equal(0, report.Difference);

        var ticket = CashCloseTicketFormatter.Format(report, 42);
        Assert.Contains("CONSUMO PERSONAL", ticket);
        Assert.Contains("Impacto en efectivo", ticket);
    }

    [Fact]
    public async Task CashCloseIgnoresCashChangeAndIncludesEntriesExits()
    {
        var product = await TestConfiguration.EnsureSampleProductAsync(_store);
        var sessionId = await _store.OpenCashSessionAsync("ana", 5000);
        await _store.RecordSaleAsync(
            [new CartItem(product, 1)], "ana", "Efectivo",
            receivedAmount: product.Price + 3000,
            printTicket: false, cashSessionId: sessionId,
            cashSessionAmount: product.Price + 3000);
        await _store.RegisterCashMovementAsync("ana", "INGRESO", 1000, "Fondo extra");
        await _store.RegisterCashMovementAsync("ana", "RETIRO", 500, "Cambio");

        var expected = 5000 + product.Price + 1000 - 500;
        await _store.CloseCashSessionAsync(expected);

        var report = await _reports.GetCashCloseReportAsync(sessionId, expected, 19m, true);
        Assert.NotNull(report);
        Assert.Equal(product.Price, report!.CashFromSales);
        Assert.Equal(1000, report.CashEntries);
        Assert.Equal(500, report.CashExits);
        Assert.Equal(expected, report.ExpectedCash);
        Assert.Equal(0, report.Difference);

        await using var db = await _factory.CreateDbContextAsync();
        var session = await db.CashSessions.SingleAsync(x => x.Id == sessionId);
        Assert.Equal(product.Price, session.TotalSales);
        Assert.Equal(0, session.Difference);
    }

    [Fact]
    public async Task CashSummaryIncludesMixtoCashPortion()
    {
        var product = await TestConfiguration.EnsureSampleProductAsync(_store);
        var sessionId = await _store.OpenCashSessionAsync("ana", 1000);
        var cashPart = Math.Round(product.Price / 2, 0, MidpointRounding.AwayFromZero);
        await _store.RecordSaleAsync(
            [new CartItem(product, 1)], "ana", "Mixto",
            receivedAmount: product.Price, printTicket: false, cashSessionId: sessionId,
            cashSessionAmount: cashPart);

        var today = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified);
        var summary = await _reports.GetCashSummaryAsync(today, today.AddDays(1), sessionId);
        Assert.Equal(cashPart, summary.CashSales);
        Assert.Equal(1000 + cashPart, summary.Expected);
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
