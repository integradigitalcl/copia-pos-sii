using System.Reflection;
using GrunflexPOS.Web.Data;
using GrunflexPOS.Web.Services;
using GrunflexPOS.Web.Services.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GrunflexPOS.Web.Tests;

/// <summary>
/// Simula ventas alternadas entre dos cajas (dos bases SQLite locales) sincronizadas
/// con el mismo catálogo central, más pruebas del espejo de stock en PosSessionState.
/// </summary>
public sealed class CrossCajaStockSyncStressTests : IDisposable
{
    private readonly string _dbPrincipal = Path.Combine(Path.GetTempPath(), $"grunflex-principal-{Guid.NewGuid():N}.db");
    private readonly string _dbAdicional = Path.Combine(Path.GetTempPath(), $"grunflex-adicional-{Guid.NewGuid():N}.db");
    private readonly LocalPosStore _principal;
    private readonly LocalPosStore _adicional;

    public CrossCajaStockSyncStressTests()
    {
        _principal = CreateStore(_dbPrincipal);
        _adicional = CreateStore(_dbAdicional);
    }

    [Fact]
    public async Task CrossCaja_AlternatingSales_StockStaysConsistent()
    {
        await SeedBothAsync();
        var seed = (await _principal.GetProductsAsync()).First();
        var centralId = seed.CentralProductId ?? seed.Id;
        var stock = 8m;

        await SyncBothAsync(centralId, seed, stock);

        var salePrincipal = await SellAsync(_principal, seed, 2);
        Assert.True(salePrincipal.Success, salePrincipal.Message);
        stock -= 2;
        await SyncBothAsync(centralId, seed, stock);

        var refreshedAdicional = await GetByCodeAsync(_adicional, seed.Code);
        Assert.Equal(stock, refreshedAdicional.Stock);

        var saleAdicional = await SellAsync(_adicional, refreshedAdicional, 3);
        Assert.True(saleAdicional.Success, saleAdicional.Message);
        stock -= 3;
        await SyncBothAsync(centralId, seed, stock);

        var refreshedPrincipal = await GetByCodeAsync(_principal, seed.Code);
        Assert.Equal(stock, refreshedPrincipal.Stock);

        var salePrincipalAgain = await SellAsync(_principal, refreshedPrincipal, 1);
        Assert.True(salePrincipalAgain.Success, salePrincipalAgain.Message);
        stock -= 1;
        await SyncBothAsync(centralId, seed, stock);

        Assert.Equal(2m, stock);
        Assert.Equal(stock, (await GetByCodeAsync(_adicional, seed.Code)).Stock);
    }

    [Fact]
    public async Task CrossCaja_ImmediateBackToBackSales_OnEachCaja_SucceedUntilStockDepleted()
    {
        await SeedBothAsync();
        var seed = (await _principal.GetProductsAsync()).First();
        var centralId = seed.CentralProductId ?? seed.Id;
        var stock = 4m;
        await SyncBothAsync(centralId, seed, stock);

        for (var round = 0; round < 4; round++)
        {
            var store = round % 2 == 0 ? _principal : _adicional;
            var product = await GetByCodeAsync(store, seed.Code);
            var sale = await SellAsync(store, product, 1);
            Assert.True(sale.Success, $"Ronda {round}: {sale.Message}");
            stock -= 1;
            await SyncBothAsync(centralId, seed, stock);
        }

        Assert.Equal(0m, (await GetByCodeAsync(_principal, seed.Code)).Stock);
        Assert.Equal(0m, (await GetByCodeAsync(_adicional, seed.Code)).Stock);

        var oversell = await SellAsync(_principal, await GetByCodeAsync(_principal, seed.Code), 1);
        Assert.False(oversell.Success);
    }

    [Fact]
    public async Task ConcurrentSync_OnSingleCaja_AppliesStockWithoutException()
    {
        await _principal.EnsureCreatedAsync();
        var seed = (await _principal.GetProductsAsync()).First();
        var centralId = seed.CentralProductId ?? seed.Id;

        var syncTasks = Enumerable.Range(0, 8)
            .Select(i => _principal.SyncCentralProductsAsync([BuildDto(centralId, seed, 20 - i)]))
            .ToArray();

        var exception = await Record.ExceptionAsync(() => Task.WhenAll(syncTasks));
        Assert.Null(exception);

        var stock = (await GetByCodeAsync(_principal, seed.Code)).Stock;
        Assert.InRange(stock, 12m, 20m);
    }

    [Fact]
    public void BuildProductLookup_ToleratesDuplicateCodes()
    {
        var method = typeof(PosSessionState).GetMethod(
            "BuildProductLookup",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var first = new PosProduct(1, "7790895000011", "Pan A", "Panadería", 1000m, 5m, "un.", "#2563EB");
        var second = new PosProduct(2, "7790895000011", "Pan B", "Panadería", 1000m, 3m, "un.", "#2563EB");
        var lookup = (Dictionary<string, PosProduct>)method!.Invoke(null, [new PosProduct[] { first, second }])!;

        Assert.Equal("Pan B", lookup["7790895000011"].Name);
        Assert.Equal(3m, lookup["7790895000011"].Stock);
    }

    [Fact]
    public void ApplyStockSnapshot_WithActiveCart_DoesNotReplaceProductsReference()
    {
        var store = CreateStore(Path.Combine(Path.GetTempPath(), $"grunflex-session-{Guid.NewGuid():N}.db"));
        var session = PosSessionTestFactory.Create(store);
        var product = new PosProduct(1, "7790895000011", "Pan", "Panadería", 1000m, 10m, "un.", "#2563EB");
        var fresh = new List<PosProduct> { product with { Stock = 3m } };

        SetProducts(session, [product]);
        AddCartLine(session, product, 1m);

        var productsBefore = GetProducts(session);
        InvokeApplyStockSnapshot(session, fresh, replaceCatalog: false, notifyUi: false);
        var productsAfter = GetProducts(session);

        Assert.Same(productsBefore, productsAfter);
        Assert.Equal(3m, GetEffectiveStock(session, product));
        Assert.Equal(3m, GetCartLines(session).Single().Product.Stock);
    }

    [Fact]
    public void ApplyStockSnapshot_WithEmptyCart_PatchesStockInPlace()
    {
        var store = CreateStore(Path.Combine(Path.GetTempPath(), $"grunflex-session-{Guid.NewGuid():N}.db"));
        var session = PosSessionTestFactory.Create(store);
        var product = new PosProduct(1, "7790895000011", "Pan", "Panadería", 1000m, 10m, "un.", "#2563EB");
        var fresh = new List<PosProduct> { product with { Stock = 4m } };

        SetProducts(session, [product]);
        InvokeApplyStockSnapshot(session, fresh, replaceCatalog: false, notifyUi: false);

        Assert.Equal(4m, GetProducts(session).Single().Stock);
        Assert.Equal(4m, GetEffectiveStock(session, product));
    }

    [Fact]
    public void ShouldBlockUiRefresh_IsTrueWhileCartHasItems()
    {
        var store = CreateStore(Path.Combine(Path.GetTempPath(), $"grunflex-session-{Guid.NewGuid():N}.db"));
        var session = PosSessionTestFactory.Create(store);
        var product = new PosProduct(1, "7790895000011", "Pan", "Panadería", 1000m, 10m, "un.", "#2563EB");

        SetProducts(session, [product]);
        Assert.False(InvokeShouldBlockUiRefresh(session));

        AddCartLine(session, product, 1m);
        Assert.True(InvokeShouldBlockUiRefresh(session));
    }

    [Fact]
    public async Task CrossCaja_SaleAfterSyncWhileBuildingCart_UsesFreshStockForValidation()
    {
        await SeedBothAsync();
        var seed = (await _principal.GetProductsAsync()).First();
        var centralId = seed.CentralProductId ?? seed.Id;
        await SyncBothAsync(centralId, seed, 2);

        var session = PosSessionTestFactory.Create(_principal);
        SetProducts(session, [seed with { Stock = 2m }]);
        AddCartLine(session, seed with { Stock = 2m }, 1m);

        await SyncBothAsync(centralId, seed, 1);
        InvokeApplyStockSnapshot(session, [seed with { Stock = 1m }], replaceCatalog: false, notifyUi: false);

        Assert.Equal(1m, GetEffectiveStock(session, seed));
        Assert.Equal(1m, GetCartLines(session).Single().Product.Stock);

        var sale = await SellAsync(_principal, seed with { Stock = 1m }, 1);
        Assert.True(sale.Success, sale.Message);
        Assert.Equal(0m, (await GetByCodeAsync(_principal, seed.Code)).Stock);
    }

    public void Dispose()
    {
        CleanupDb(_dbPrincipal);
        CleanupDb(_dbAdicional);
    }

    private async Task SeedBothAsync()
    {
        await _principal.EnsureCreatedAsync();
        await _adicional.EnsureCreatedAsync();
    }

    private async Task SyncBothAsync(int centralId, PosProduct seed, decimal stock)
    {
        var dto = BuildDto(centralId, seed, stock);
        await _principal.SyncCentralProductsAsync([dto]);
        await _adicional.SyncCentralProductsAsync([dto]);
    }

    private static MulticajaProductDto BuildDto(int centralId, PosProduct seed, decimal stock) =>
        new()
        {
            Id = centralId,
            CodigoBarras = seed.Code,
            Nombre = seed.Name,
            Costo = seed.Cost,
            Precio = seed.Price,
            PrecioMayoreo = seed.WholesalePrice,
            Stock = (int)stock,
            InvMinimo = (int)seed.MinStock,
            InvMaximo = (int)seed.MaxStock,
            TipoVenta = seed.SaleType,
            Departamento = seed.Department
        };

    private static async Task<SaleResult> SellAsync(LocalPosStore store, PosProduct product, decimal qty) =>
        await store.RecordSaleAsync([new CartItem(product, qty)], "admin", "Efectivo", printTicket: false);

    private static async Task<PosProduct> GetByCodeAsync(LocalPosStore store, string code) =>
        (await store.GetProductsAsync()).Single(x => x.Code == code);

    [Fact]
    public async Task AddProduct_AfterSaleMessageTimerDisposed_DoesNotThrow()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"grunflex-session-{Guid.NewGuid():N}.db");
        var store = CreateStore(dbPath);
        await store.EnsureCreatedAsync();
        var session = PosSessionTestFactory.Create(store);
        var product = (await store.GetProductsAsync()).First();

        var schedule = typeof(PosSessionState).GetMethod(
            "ScheduleSaleMessageDismissal",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(schedule);
        schedule!.Invoke(session, null);

        await Task.Delay(50);
        var dismissField = typeof(PosSessionState).GetField(
            "_saleMessageCancellation",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var cts = (CancellationTokenSource?)dismissField!.GetValue(session);
        Assert.NotNull(cts);
        cts!.Dispose();

        var exception = Record.Exception(() => session.AddProduct(product));
        Assert.Null(exception);

        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
        catch
        {
            /* cleanup */
        }
    }

    private static LocalPosStore CreateStore(string dbPath)
    {
        var options = new DbContextOptionsBuilder<LocalPosDbContext>()
            .UseSqlite($"Data Source={dbPath};Pooling=false")
            .Options;
        return TestConfiguration.CreateStore(options);
    }

    private static void CleanupDb(string path)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
        catch
        {
            /* best effort */
        }
    }

    private static void SetProducts(PosSessionState session, IReadOnlyList<PosProduct> products) =>
        typeof(PosSessionState).GetProperty(nameof(PosSessionState.Products))!
            .SetValue(session, products);

    private static IReadOnlyList<PosProduct> GetProducts(PosSessionState session) =>
        (IReadOnlyList<PosProduct>)typeof(PosSessionState).GetProperty(nameof(PosSessionState.Products))!
            .GetValue(session)!;

    private static void AddCartLine(PosSessionState session, PosProduct product, decimal qty)
    {
        var cartField = typeof(PosSessionState).GetField("_cart", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cart = (List<CartLine>)cartField.GetValue(session)!;
        cart.Add(new CartLine(product, qty, product.Price));
    }

    private static IReadOnlyList<CartLine> GetCartLines(PosSessionState session)
    {
        var cartField = typeof(PosSessionState).GetField("_cart", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (List<CartLine>)cartField.GetValue(session)!;
    }

    private static decimal GetEffectiveStock(PosSessionState session, PosProduct product) =>
        (decimal)typeof(PosSessionState).GetMethod(nameof(PosSessionState.GetEffectiveStock))!
            .Invoke(session, [product])!;

    private static void InvokeApplyStockSnapshot(
        PosSessionState session,
        IReadOnlyList<PosProduct> fresh,
        bool replaceCatalog,
        bool notifyUi)
    {
        var method = typeof(PosSessionState).GetMethod(
            "ApplyStockSnapshot",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(session, [fresh, replaceCatalog, notifyUi]);
    }

    private static bool InvokeShouldBlockUiRefresh(PosSessionState session)
    {
        var method = typeof(PosSessionState).GetMethod(
            "ShouldBlockUiRefresh",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (bool)method!.Invoke(session, null)!;
    }
}

internal static class PosSessionTestFactory
{
    public static PosSessionState Create(LocalPosStore store)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var httpClient = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
        var httpFactory = new SingleHttpClientFactory(httpClient);
        var hostEnvironment = new TestHostEnvironment();
        var licenseService = new WebLicenseService(
            store, config, httpFactory, NullLogger<WebLicenseService>.Instance);
        var licenseState = new WebLicenseState(licenseService, store, config);
        var offlineQueue = new MulticajaOfflineQueue(NullLogger<MulticajaOfflineQueue>.Instance);
        var tokenResolver = new BridgeTokenResolver(
            config, hostEnvironment, NullLogger<BridgeTokenResolver>.Instance);
        var hardware = new HardwareBridgeClient(
            httpClient, config, NullLogger<HardwareBridgeClient>.Instance, store, tokenResolver);
        var boleta = new BoletaPdfService(NullLogger<BoletaPdfService>.Instance);
        var multicaja = new MulticajaClient(
            httpClient, config, store, offlineQueue, licenseState, NullLogger<MulticajaClient>.Instance);
        var licensingCloud = new LicensingCloudClient(
            httpFactory, store, licenseService, licenseState, config, NullLogger<LicensingCloudClient>.Instance);
        var email = new PosEmailService(store, boleta, NullLogger<PosEmailService>.Instance);
        var invoice = new InvoiceEmissionService(store, httpFactory, NullLogger<InvoiceEmissionService>.Instance);

        return new PosSessionState(
            store, hardware, boleta, multicaja, licenseState, licensingCloud, email, invoice,
            NullLogger<PosSessionState>.Instance);
    }

    private sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "GrunflexPOS.Web.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new PhysicalFileProvider(AppContext.BaseDirectory);
    }
}
