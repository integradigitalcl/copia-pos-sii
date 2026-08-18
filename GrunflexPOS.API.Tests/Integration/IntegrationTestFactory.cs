using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GrunflexPOS.API.Tests.Integration;

/// <summary>API con SQLite temporal y par RSA en memoria para pruebas sin depender de postgres ni api.secrets.json.</summary>
public sealed class IntegrationTestFactory : WebApplicationFactory<Program>, IDisposable
{
    private readonly string _dbPath;
    private readonly string _commerceDbPath;
    private readonly string _privatePem;
    private bool _disposed;
    private bool _initialized;
    private readonly SemaphoreSlim _readyGate = new(1, 1);

    public IntegrationTestFactory()
    {
        var suffix = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"gf_api_test_{suffix}.db");
        _commerceDbPath = Path.Combine(Path.GetTempPath(), $"gf_commerce_test_{suffix}.db");
        using var rsa = RSA.Create(2048);
        _privatePem = rsa.ExportRSAPrivateKeyPem();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        var pos = scope.ServiceProvider.GetRequiredService<GrunflexPOS.API.Data.PosCommerceDbContext>();
        pos.Database.EnsureCreated();
        return host;
    }

    public async Task EnsureHostReadyAsync(CancellationToken ct = default)
    {
        if (_initialized)
            return;

        await _readyGate.WaitAsync(ct);
        try
        {
            if (_initialized)
                return;

            using var client = CreateClient();
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    var response = await client.GetAsync("/health/live", ct);
                    if (response.IsSuccessStatusCode)
                    {
                        _initialized = true;
                        return;
                    }
                }
                catch
                {
                    /* host still starting */
                }

                await Task.Delay(100, ct);
            }

            throw new InvalidOperationException("API host not ready for integration tests.");
        }
        finally
        {
            _readyGate.Release();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = $"Data Source={_dbPath};Cache=Shared",
                ["ConnectionStrings:Pos"] = $"Data Source={_commerceDbPath};Cache=Shared",
                ["Jwt:Issuer"] = "grunflexpos.api",
                ["Jwt:Audience"] = "grunflexpos.clients",
                ["Jwt:SigningKey"] = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
                ["Licensing:PrivateKeyPem"] = _privatePem,
                ["Licensing:IssuerApiKey"] = "",
                ["Security:AdminPassword"] = "test-admin-integration",
                ["Security:AdminUser"] = "admin",
                ["Multicaja:RequireSharedSecret"] = "false"
            });
        });
    }

    public new HttpClient CreateClient()
    {
        var client = base.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(2);
        return client;
    }

    public new void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        base.Dispose();
        TryDeleteDb(_dbPath);
        TryDeleteDb(_commerceDbPath);
        GC.SuppressFinalize(this);
    }

    private static void TryDeleteDb(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            /* test cleanup best-effort */
        }
    }
}
