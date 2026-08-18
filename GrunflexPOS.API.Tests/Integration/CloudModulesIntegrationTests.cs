using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace GrunflexPOS.API.Tests.Integration;

public class CloudModulesIntegrationTests : IClassFixture<IntegrationTestFactory>
{
    private readonly IntegrationTestFactory _factory;

    public CloudModulesIntegrationTests(IntegrationTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Licensing_Status_UnknownActivation_ReturnsFoundFalse()
    {
        await _factory.EnsureHostReadyAsync();
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/licensing/status?activationId=NO-EXISTE-123");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        doc.RootElement.GetProperty("found").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Licensing_Activate_AfterSeed_ReturnsGfv2Token()
    {
        await _factory.EnsureHostReadyAsync();
        using var client = _factory.CreateClient();
        var exp = DateTime.UtcNow.AddYears(1);

        var activationId = $"INT-TEST-{Guid.NewGuid():N}"[..24];
        var seed = new
        {
            activationId,
            customerName = "Cliente",
            businessName = "Negocio",
            licenseType = "Suscripción",
            numberOfBoxes = 1,
            expUtc = exp,
            multicaja = false,
            onlineSupport = true,
            cloudBackup = true,
            prioritySupport = false,
            licenseToken = new string('x', 400)
        };

        var create = await client.PostAsJsonAsync("/api/LicenseIssuer", seed);
        var createBody = await create.Content.ReadAsStringAsync();
        create.StatusCode.Should().BeOneOf(new[] { HttpStatusCode.Created, HttpStatusCode.OK }, createBody);

        var activate = new { activationId, machineName = Environment.MachineName };
        var actResp = await client.PostAsJsonAsync("/api/licensing/activate", activate);

        actResp.StatusCode.Should().Be(HttpStatusCode.OK, await actResp.Content.ReadAsStringAsync());
        var json = await actResp.Content.ReadAsStringAsync();
        json.Should().Contain("GFv2.");
        json.Should().Contain("licenseToken");
    }

    [Fact]
    public async Task Support_Ticket_UnknownActivation_ReturnsBadRequest()
    {
        await _factory.EnsureHostReadyAsync();
        using var client = _factory.CreateClient();
        var body = new { activationId = "UNKNOWN", subject = "Ayuda", body = "1234567890 detalle mínimo" };
        var response = await client.PostAsJsonAsync("/api/support/tickets", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
