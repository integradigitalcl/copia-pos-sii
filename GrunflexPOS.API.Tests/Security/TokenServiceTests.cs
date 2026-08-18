using FluentAssertions;
using GrunflexPOS.API.Security;
using Microsoft.Extensions.Options;
using Xunit;

namespace GrunflexPOS.API.Tests.Security;

public class TokenServiceTests
{
    [Fact]
    public void CreateAccessToken_ShouldReturnJwt()
    {
        var options = Options.Create(new JwtOptions
        {
            Issuer = "test-issuer",
            Audience = "test-audience",
            SigningKey = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
            AccessTokenMinutes = 5
        });

        var sut = new TokenService(options);
        var token = sut.CreateAccessToken("tester", "admin");

        token.Should().NotBeNullOrWhiteSpace();
        token.Split('.').Length.Should().Be(3);
    }
}
