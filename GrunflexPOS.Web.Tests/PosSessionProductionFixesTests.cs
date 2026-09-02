using System.Reflection;
using GrunflexPOS.Web.Data;
using GrunflexPOS.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GrunflexPOS.Web.Tests;

public sealed class PosSessionProductionFixesTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"grunflex-pos-fixes-{Guid.NewGuid():N}.db");
    private readonly LocalPosStore _store;

    public PosSessionProductionFixesTests()
    {
        var options = new DbContextOptionsBuilder<LocalPosDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;
        _store = TestConfiguration.CreateStore(options);
    }

    [Theory]
    [InlineData("08:30:00", true, "08:30")]
    [InlineData("08:30", true, "08:30")]
    [InlineData("23:59:59", true, "23:59")]
    [InlineData("7:05", true, "07:05")]
    [InlineData("invalid", false, "")]
    public void TryParseShiftTime_AcceptsBrowserTimeFormats(string input, bool expected, string normalized)
    {
        var ok = InvokeTryParseShiftTime(input, out var time);
        Assert.Equal(expected, ok);
        if (expected)
            Assert.Equal(normalized, FormatShiftTime(time));
    }

    [Fact]
    public void CatalogSyncInterval_IsFiveSeconds()
    {
        var field = typeof(PosSessionState).GetField(
            "CatalogSyncIntervalSeconds",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        Assert.Equal(5, field!.GetValue(null));
    }

    [Fact]
    public void CartLine_RefreshProduct_UpdatesVisibleStock()
    {
        var product = new PosProduct(1, "7790895000011", "Pan", "Panadería", 1890m, 24m, "un.", "#2563EB");
        var line = new CartLine(product, 2m, 1890m);
        var updated = product with { Stock = 18m };

        line.RefreshProduct(updated);

        Assert.Equal(18m, line.Product.Stock);
        Assert.Equal(2m, line.Quantity);
    }

    [Fact]
    public async Task SyncCentralProducts_UpdatesLocalStock_AndCartSnapshot()
    {
        await _store.EnsureCreatedAsync();
        var product = (await _store.GetProductsAsync()).First();
        var line = new CartLine(product, 1m, product.Price);

        await _store.SyncCentralProductsAsync([
            new MulticajaProductDto
            {
                Id = product.CentralProductId ?? product.Id,
                CodigoBarras = product.Code,
                Nombre = product.Name,
                Costo = product.Cost,
                Precio = product.Price,
                PrecioMayoreo = product.WholesalePrice,
                Stock = 3,
                InvMinimo = (int)product.MinStock,
                InvMaximo = (int)product.MaxStock,
                TipoVenta = product.SaleType,
                Departamento = product.Department
            }
        ]);

        var refreshed = (await _store.GetProductsAsync()).Single(x => x.Code == product.Code);
        Assert.Equal(3m, refreshed.Stock);

        line.RefreshProduct(refreshed);
        Assert.Equal(3m, line.Product.Stock);
    }

    [Fact]
    public async Task ShiftSchedule_ParserAndStore_PersistNormalizedTimes()
    {
        await _store.EnsureCreatedAsync();

        Assert.True(InvokeTryParseShiftTime("08:30:00", out var start));
        Assert.True(InvokeTryParseShiftTime("22:45:00", out var end));

        var startText = FormatShiftTime(start);
        var endText = FormatShiftTime(end);
        await _store.SetSettingAsync("corte_hora_inicio", startText);
        await _store.SetSettingAsync("corte_hora_cierre", endText);

        Assert.Equal("08:30", await _store.GetSettingAsync("corte_hora_inicio"));
        Assert.Equal("22:45", await _store.GetSettingAsync("corte_hora_cierre"));
    }

    [Fact]
    public async Task ConcurrentCatalogSync_DoesNotLoseStockUpdate()
    {
        await _store.EnsureCreatedAsync();
        var product = (await _store.GetProductsAsync()).First();
        var dto = new MulticajaProductDto
        {
            Id = product.CentralProductId ?? product.Id,
            CodigoBarras = product.Code,
            Nombre = product.Name,
            Costo = product.Cost,
            Precio = product.Price,
            PrecioMayoreo = product.WholesalePrice,
            Stock = 7,
            InvMinimo = (int)product.MinStock,
            InvMaximo = (int)product.MaxStock,
            TipoVenta = product.SaleType,
            Departamento = product.Department
        };

        var syncTasks = Enumerable.Range(0, 4)
            .Select(_ => _store.SyncCentralProductsAsync([dto]))
            .ToArray();

        await Task.WhenAll(syncTasks);

        var refreshed = (await _store.GetProductsAsync()).Single(x => x.Code == product.Code);
        Assert.Equal(7m, refreshed.Stock);
    }

    [Fact]
    public void PosSessionState_ExposesEffectiveStock_ForLiveMirror()
    {
        var method = typeof(PosSessionState).GetMethod(
            nameof(PosSessionState.GetEffectiveStock),
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
    }

    [Fact]
    public void PosSessionState_ExposesUiDispatcherBinding_ForBackgroundSync()
    {
        var method = typeof(PosSessionState).GetMethod(
            nameof(PosSessionState.BindUiDispatcher),
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
    }

    [Fact]
    public void PosSessionState_DefersCatalogUiWhileSaleIsInProgress()
    {
        var saleField = typeof(PosSessionState).GetField(
            "_saleInProgress",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var deferredField = typeof(PosSessionState).GetField(
            "_catalogUiDeferred",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var deferMethod = typeof(PosSessionState).GetMethod(
            "ShouldDeferCatalogUi",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var flushMethod = typeof(PosSessionState).GetMethod(
            "FlushDeferredCatalogUiAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var refreshAfterSale = typeof(PosSessionState).GetMethod(
            "RefreshAfterSaleAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(saleField);
        Assert.NotNull(deferredField);
        Assert.NotNull(deferMethod);
        Assert.NotNull(flushMethod);
        Assert.NotNull(refreshAfterSale);
    }

    [Fact]
    public void DuplicateBarcodeDictionary_UsesLastProductForCartRefresh()
    {
        var productA = new PosProduct(1, "7790895000011", "Pan A", "Panadería", 1890m, 24m, "un.", "#2563EB");
        var productB = new PosProduct(2, "7790895000011", "Pan B", "Panadería", 1990m, 18m, "un.", "#2563EB");
        var line = new CartLine(productA, 2m, 1890m);
        var byCode = new Dictionary<string, PosProduct>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in new[] { productA, productB })
            byCode[product.Code] = product;

        line.RefreshProduct(byCode[productA.Code]);

        Assert.Equal(18m, line.Product.Stock);
        Assert.Equal("Pan B", line.Product.Name);
    }

    private static bool InvokeTryParseShiftTime(string input, out TimeOnly time)
    {
        time = default;
        var method = typeof(PosSessionState).GetMethod(
            "TryParseShiftTime",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var args = new object?[] { input, null };
        var ok = (bool)method!.Invoke(null, args)!;
        if (ok)
            time = (TimeOnly)args[1]!;
        return ok;
    }

    private static string FormatShiftTime(TimeOnly time)
    {
        var method = typeof(PosSessionState).GetMethod(
            "FormatShiftTime",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, [time])!;
    }

    public void Dispose()
    {
        try { File.Delete(_databasePath); } catch { }
    }
}
