namespace GrunflexPOS.API.DTOs;

public sealed class AuthTokenResponse
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
}
