using GrunflexPOS.API.Data;
using GrunflexPOS.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GrunflexPOS.API.Security;

public sealed class RefreshTokenService
{
    private readonly ApiDbContext _db;
    private readonly TokenService _tokenService;
    private readonly JwtOptions _jwt;

    public RefreshTokenService(ApiDbContext db, TokenService tokenService, IOptions<JwtOptions> jwt)
    {
        _db = db;
        _tokenService = tokenService;
        _jwt = jwt.Value;
    }

    public async Task<string> CreateAsync(string username, string role, CancellationToken cancellationToken = default)
    {
        var token = _tokenService.CreateRefreshToken();
        var entity = new RefreshTokenEntity
        {
            Username = username,
            Role = role,
            TokenHash = _tokenService.HashToken(token),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(_jwt.RefreshTokenDays)
        };

        _db.RefreshTokens.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return token;
    }

    public async Task<(bool ok, string username, string role, RefreshTokenEntity? entity)> ValidateAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var hash = _tokenService.HashToken(refreshToken);
        var entity = await _db.RefreshTokens
            .FirstOrDefaultAsync(x => x.TokenHash == hash, cancellationToken);

        if (entity == null || entity.RevokedAtUtc.HasValue || entity.ExpiresAtUtc <= DateTime.UtcNow)
            return (false, string.Empty, string.Empty, null);

        return (true, entity.Username, entity.Role, entity);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var hash = _tokenService.HashToken(refreshToken);
        var entity = await _db.RefreshTokens.FirstOrDefaultAsync(x => x.TokenHash == hash, cancellationToken);
        if (entity == null || entity.RevokedAtUtc.HasValue)
            return;

        entity.RevokedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RevokeAndReplaceAsync(RefreshTokenEntity current, Guid replacementId, CancellationToken cancellationToken = default)
    {
        current.RevokedAtUtc = DateTime.UtcNow;
        current.ReplacedById = replacementId;
        await _db.SaveChangesAsync(cancellationToken);
    }
}
