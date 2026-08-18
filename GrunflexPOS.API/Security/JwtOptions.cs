namespace GrunflexPOS.API.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "grunflexpos.api";
    public string Audience { get; set; } = "grunflexpos.clients";
    public string SigningKey { get; set; } = string.Empty;
    public List<string> PreviousSigningKeys { get; set; } = new();
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 15;
}
