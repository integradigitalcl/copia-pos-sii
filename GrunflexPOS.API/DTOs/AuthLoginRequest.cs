namespace GrunflexPOS.API.DTOs;

public sealed class AuthLoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}
