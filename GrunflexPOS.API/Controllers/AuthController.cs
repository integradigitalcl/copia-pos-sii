using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GrunflexPOS.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly TokenService _tokenService;
    private readonly RefreshTokenService _refreshTokens;
    private readonly JwtOptions _jwt;
    private readonly IConfiguration _configuration;

    public AuthController(
        TokenService tokenService,
        RefreshTokenService refreshTokens,
        IOptions<JwtOptions> jwt,
        IConfiguration configuration)
    {
        _tokenService = tokenService;
        _refreshTokens = refreshTokens;
        _jwt = jwt.Value;
        _configuration = configuration;
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<AuthTokenResponse>> Login([FromBody] AuthLoginRequest request, CancellationToken cancellationToken)
    {
        var adminUser = _configuration["Security:AdminUser"] ?? "admin";
        var adminPassword = _configuration["Security:AdminPassword"];

        if (string.IsNullOrWhiteSpace(adminPassword))
            return StatusCode(StatusCodes.Status500InternalServerError, "Admin password no configurado.");

        if (!string.Equals(request.Username, adminUser, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(request.Password, adminPassword, StringComparison.Ordinal))
            return Unauthorized("Credenciales inválidas");

        return Ok(await IssueTokensAsync(request.Username, "admin", cancellationToken));
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<ActionResult<AuthTokenResponse>> Refresh([FromBody] AuthRefreshRequest request, CancellationToken cancellationToken)
    {
        var validation = await _refreshTokens.ValidateAsync(request.RefreshToken, cancellationToken);
        if (!validation.ok || validation.entity == null)
            return Unauthorized("Refresh token inválido o expirado.");

        var response = await IssueTokensAsync(validation.username, validation.role, cancellationToken);

        // Marcar token antiguo como revocado y apuntar al nuevo.
        var newValidation = await _refreshTokens.ValidateAsync(response.RefreshToken, cancellationToken);
        if (newValidation.entity != null)
            await _refreshTokens.RevokeAndReplaceAsync(validation.entity, newValidation.entity.Id, cancellationToken);

        return Ok(response);
    }

    [Authorize]
    [HttpPost("revoke")]
    public async Task<IActionResult> Revoke([FromBody] AuthRefreshRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
            await _refreshTokens.RevokeAsync(request.RefreshToken, cancellationToken);
        return NoContent();
    }

    private async Task<AuthTokenResponse> IssueTokensAsync(string username, string role, CancellationToken cancellationToken)
    {
        var access = _tokenService.CreateAccessToken(username, role);
        var refresh = await _refreshTokens.CreateAsync(username, role, cancellationToken);

        return new AuthTokenResponse
        {
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(_jwt.AccessTokenMinutes)
        };
    }
}
