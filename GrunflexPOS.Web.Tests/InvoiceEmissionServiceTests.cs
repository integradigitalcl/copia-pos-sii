using System.Net;
using GrunflexPOS.Web.Data;
using GrunflexPOS.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GrunflexPOS.Web.Tests;

public sealed class InvoiceEmissionServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"grunflex-inv-test-{Guid.NewGuid():N}.db");
    private readonly LocalPosStore _store;
    private readonly string _mockUrl;
    private readonly HttpListenerHost _host;

    public InvoiceEmissionServiceTests()
    {
        var options = new DbContextOptionsBuilder<LocalPosDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=false")
            .Options;
        _store = TestConfiguration.CreateStore(options);
        _host = HttpListenerHost.Start();
        _mockUrl = _host.Url;
    }

    [Fact]
    public async Task ProbeAsync_ReachesMockReceiver()
    {
        await _store.EnsureCreatedAsync();
        await _store.SetSettingAsync("facturacion_endpoint", _mockUrl);
        var service = CreateService();
        var (ok, message) = await service.ProbeAsync();
        Assert.True(ok);
        Assert.Contains("200", message);
    }

    [Fact]
    public async Task EmitTestAsync_PostsDocumentAndParsesResponse()
    {
        await _store.EnsureCreatedAsync();
        await _store.SetSettingsAsync(new Dictionary<string, string>
        {
            ["facturacion_activa"] = "true",
            ["facturacion_endpoint"] = _mockUrl,
            ["facturacion_tipo_documento"] = "boleta",
            ["facturacion_emisor_rut"] = "76.000.000-0",
            ["facturacion_emisor_razon"] = "Grunflex Test",
            ["facturacion_iva_porcentaje"] = "19",
            ["facturacion_precios_con_iva"] = "true"
        });

        var service = CreateService();
        var (ok, message) = await service.EmitTestAsync();

        Assert.True(ok);
        Assert.Contains("MOCK-", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmitSaleAsync_PersistsEmissionHistory()
    {
        await _store.EnsureCreatedAsync();
        await _store.SetSettingsAsync(new Dictionary<string, string>
        {
            ["facturacion_activa"] = "true",
            ["facturacion_endpoint"] = _mockUrl,
            ["facturacion_tipo_documento"] = "boleta"
        });

        var product = new PosProduct(1, "P1", "Pan", "Panadería", 1000, 10, "un.", "#000");
        var service = CreateService();
        var (ok, _) = await service.EmitSaleAsync(
            42, [new CartItem(product, 2)], 2000m, "Efectivo", "admin");

        Assert.True(ok);
        var history = await _store.GetRecentInvoiceEmissionsAsync();
        Assert.Contains(history, x => x.TicketNumber == 42 && x.Status == "ok");
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
        _host.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_databasePath); } catch { /* best effort */ }
    }

    private sealed class HttpListenerHost : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public string Url { get; }

        private HttpListenerHost(string url)
        {
            Url = url;
            _listener.Prefixes.Add(url.EndsWith('/') ? url : url + "/");
            _listener.Start();
            _loop = Task.Run(ListenAsync);
        }

        public static HttpListenerHost Start()
        {
            for (var port = 18700; port < 18800; port++)
            {
                try
                {
                    return new HttpListenerHost($"http://127.0.0.1:{port}/invoice");
                }
                catch
                {
                    // try next port
                }
            }

            throw new InvalidOperationException("No free port for invoice mock.");
        }

        private async Task ListenAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (ctx.Request.HttpMethod == "GET")
                        {
                            var bytes = """{"status":"ok"}"""u8.ToArray();
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "application/json";
                            await ctx.Response.OutputStream.WriteAsync(bytes);
                        }
                        else
                        {
                            using var reader = new StreamReader(ctx.Request.InputStream);
                            _ = await reader.ReadToEndAsync();
                            var body = """{"success":true,"providerDocumentId":"MOCK-TEST01","folio":"100","message":"ok"}"""u8.ToArray();
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "application/json";
                            await ctx.Response.OutputStream.WriteAsync(body);
                        }
                    }
                    finally
                    {
                        ctx.Response.Close();
                    }
                });
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _listener.Close();
            _cts.Dispose();
        }
    }
}
