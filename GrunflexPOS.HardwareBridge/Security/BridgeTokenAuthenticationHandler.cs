using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace GrunflexPOS.HardwareBridge.Security;

public sealed class BridgeTokenAuthenticationHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly BridgeTokenStore _tokenStore;

    public BridgeTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        BridgeTokenStore tokenStore)
        : base(options, logger, encoder)
    {
        _tokenStore = tokenStore;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var headerValue) ||
            !AuthenticationHeaderValue.TryParse(headerValue.ToString(), out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var supplied = Encoding.UTF8.GetBytes(header.Parameter);
        var expected = Encoding.UTF8.GetBytes(_tokenStore.GetToken());
        var valid = supplied.Length == expected.Length &&
                    CryptographicOperations.FixedTimeEquals(supplied, expected);

        if (!valid)
            return Task.FromResult(AuthenticateResult.Fail("Invalid bridge token."));

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "hardware-bridge-client") },
            Scheme.Name);
        return Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
