using GrunflexPOS.Web.Data;
using GrunflexPOS.Web.Services;
using GrunflexPOS.Web.Services.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GrunflexPOS.Web.Tests;

/// <summary>Requiere API en http://127.0.0.1:7279 con licencia GF-WEB-E2E-6036.</summary>
public sealed class WebLicensingCloudE2eTests
{
    private const string ApiBaseUrl = "http://127.0.0.1:7279/";
    private const string ActivationId = "GF-WEB-E2E-6036";

    [Fact]
    public async Task CloudActivate_AppliesToken_AndEnablesModules()
    {
        if (!await ApiIsReachableAsync())
        {
            // Entorno sin API levantada: no fallar CI local.
            return;
        }

        var dbPath = Path.Combine(Path.GetTempPath(), $"grunflex-cloud-e2e-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<LocalPosDbContext>()
            .UseSqlite($"Data Source={dbPath};Pooling=false")
            .Options;
        var store = TestConfiguration.CreateStore(options);
        await store.EnsureCreatedAsync();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Licensing:ApiBaseUrl"] = ApiBaseUrl,
                ["Licensing:RequireLicense"] = "true",
                ["Licensing:OfflineGraceDays"] = "14"
            })
            .Build();

        var httpFactory = new TestHttpClientFactory();
        var licenseService = new WebLicenseService(store, config, httpFactory, NullLogger<WebLicenseService>.Instance);
        var state = new WebLicenseState(licenseService, store, config);
        var cloud = new LicensingCloudClient(httpFactory, store, licenseService, state, config, NullLogger<LicensingCloudClient>.Instance);

        await store.SetSettingAsync("licencia_activation_id", ActivationId);

        var activated = await cloud.ActivateAsync(ActivationId);
        Assert.True(activated.Ok, activated.Message);

        await state.RefreshAsync();
        Assert.True(state.IsValid);
        Assert.True(state.Multicaja);
        Assert.True(state.CloudBackup);
        Assert.True(state.OnlineSupport);
        Assert.Equal(5, state.NumberOfBoxes);
        Assert.True(state.IsPosAccessAllowed);

        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
        catch
        {
            /* cleanup best effort */
        }
    }

    private static async Task<bool> ApiIsReachableAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync($"{ApiBaseUrl.TrimEnd('/')}/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private sealed class TestDbFactory(DbContextOptions<LocalPosDbContext> options) : IDbContextFactory<LocalPosDbContext>
    {
        public LocalPosDbContext CreateDbContext() => new(options);
        public Task<LocalPosDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalPosDbContext(options));
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
