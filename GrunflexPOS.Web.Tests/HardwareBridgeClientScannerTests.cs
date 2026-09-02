using System.Net;
using System.Text;
using GrunflexPOS.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GrunflexPOS.Web.Tests;

public sealed class HardwareBridgeClientScannerTests
{
    [Fact]
    public async Task ListSerialPorts_ReturnsNames_FromBridge()
    {
        var handler = new StubHandler(_ =>
            JsonResponse("""[{"name":"COM3"},{"name":"COM5"}]"""));
        var client = CreateClient(handler);

        var ports = await client.ListSerialPortsAsync();

        Assert.Equal(["COM3", "COM5"], ports);
        Assert.Equal("/api/serial-ports", handler.LastPath);
    }

    [Fact]
    public async Task ConnectScanner_PostsSerialSettings()
    {
        var handler = new StubHandler(_ =>
            JsonResponse("""{"connected":true,"port":"COM3"}"""));
        var client = CreateClient(handler);

        var result = await client.ConnectScannerAsync("COM3", 9600, 8, "None", "One", "None");

        Assert.True(result.Success);
        Assert.Contains("COM3", result.Message);
        Assert.Equal("/api/scanner/connect", handler.LastPath);
        Assert.Contains("COM3", handler.LastBody ?? string.Empty);
    }

    [Fact]
    public void TryParseScannerEvent_ReadsCodePayload()
    {
        var ok = HardwareBridgeClient.TryParseScannerEvent(
            """{"type":"code","code":"7501234567890","port":"COM3"}""",
            "scanner",
            out var parsed);

        Assert.True(ok);
        Assert.NotNull(parsed);
        Assert.Equal("code", parsed.Type);
        Assert.Equal("7501234567890", parsed.Code);
        Assert.Equal("COM3", parsed.Port);
    }

    private static HardwareBridgeClient CreateClient(StubHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:7390/") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HardwareBridge:Token"] = "test-token-1234567890",
            ["HardwareBridge:Url"] = "http://127.0.0.1:7390/"
        }).Build();
        var env = new TestHostEnvironment { EnvironmentName = Environments.Development };
        var resolver = new BridgeTokenResolver(config, env, NullLogger<BridgeTokenResolver>.Instance);
        return new HardwareBridgeClient(http, config, NullLogger<HardwareBridgeClient>.Instance, settings: null!, resolver);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastPath { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }
}
