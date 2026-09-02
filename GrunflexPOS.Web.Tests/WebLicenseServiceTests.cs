using System.Security.Cryptography;
using Grunflex.Licensing;
using GrunflexPOS.Web.Data;
using GrunflexPOS.Web.Services;
using GrunflexPOS.Web.Services.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GrunflexPOS.Web.Tests;

public sealed class WebLicenseServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"grunflex-lic-test-{Guid.NewGuid():N}.db");
    private readonly LocalPosStore _store;
    private readonly string _publicPem;
    private readonly string _privatePem;

    public WebLicenseServiceTests()
    {
        using var rsa = RSA.Create(2048);
        _publicPem = rsa.ExportRSAPublicKeyPem();
        _privatePem = rsa.ExportRSAPrivateKeyPem();

        var options = new DbContextOptionsBuilder<LocalPosDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=false")
            .Options;
        _store = TestConfiguration.CreateStore(options);
    }

    [Fact]
    public async Task ValidGfv2Token_IsAcceptedAndPersistsModules()
    {
        await _store.EnsureCreatedAsync();
        var token = SignToken(new GrunflexLicensePayload
        {
            Customer = "Cliente Test",
            Machine = Environment.MachineName,
            ExpUtc = DateTime.UtcNow.AddDays(30),
            Multicaja = true,
            OnlineSupport = true,
            ActivationId = "TEST-ACTIVATION",
            OfflineGraceDays = 14,
            NumberOfBoxes = 3
        });

        var service = CreateService(_publicPem);
        var result = await service.TryValidateAndApplyAsync(token);
        Assert.True(result.Success);

        var state = new WebLicenseState(service, _store, CreateConfig(requireLicense: true));
        await state.RefreshAsync();

        Assert.True(state.IsValid);
        Assert.True(state.Multicaja);
        Assert.True(state.OnlineSupport);
        Assert.Equal(3, state.NumberOfBoxes);
        Assert.Equal("TEST-ACTIVATION", state.ActivationId);
    }

    [Fact]
    public async Task ExpiredToken_IsRejected()
    {
        await _store.EnsureCreatedAsync();
        var token = SignToken(new GrunflexLicensePayload
        {
            Customer = "Vencida",
            ExpUtc = DateTime.UtcNow.AddDays(-1),
            Multicaja = true
        });

        var service = CreateService(_publicPem);
        var result = await service.TryValidateAndApplyAsync(token);
        Assert.False(result.Success);
        Assert.Contains("vencida", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongMachine_IsRejected()
    {
        await _store.EnsureCreatedAsync();
        var token = SignToken(new GrunflexLicensePayload
        {
            Customer = "Otro PC",
            Machine = "OTRO-PC-INEXISTENTE",
            ExpUtc = DateTime.UtcNow.AddDays(10),
            Multicaja = true
        });

        var service = CreateService(_publicPem);
        var result = await service.TryValidateAndApplyAsync(token);
        Assert.False(result.Success);
        Assert.Contains("equipo", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(_databasePath))
                File.Delete(_databasePath);
        }
        catch
        {
            /* test cleanup is best effort */
        }
    }

    private string SignToken(GrunflexLicensePayload payload)
    {
        using var rsa = GrunflexLicenseCodec.ImportPrivateKeyFromPem(_privatePem);
        return GrunflexLicenseCodec.EncodeV2(payload, rsa);
    }

    private WebLicenseService CreateService(string publicPem)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Licensing:PublicKeyPem"] = publicPem,
                ["Licensing:OfflineGraceDays"] = "14"
            })
            .Build();

        return new WebLicenseService(
            _store,
            config,
            new TestHttpClientFactory(),
            NullLogger<WebLicenseService>.Instance);
    }

    private static IConfiguration CreateConfig(bool requireLicense) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Licensing:RequireLicense"] = requireLicense.ToString().ToLowerInvariant()
            })
            .Build();

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
