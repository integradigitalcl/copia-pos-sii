using System.Net;
using FluentAssertions;
using Xunit;

namespace GrunflexPOS.API.Tests.Integration;

public class HealthEndpointTests : IClassFixture<IntegrationTestFactory>
{
    private readonly IntegrationTestFactory _factory;

    public HealthEndpointTests(IntegrationTestFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_ShouldReturnSuccessOrServiceUnavailable()
    {
        await _factory.EnsureHostReadyAsync();
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }
}
