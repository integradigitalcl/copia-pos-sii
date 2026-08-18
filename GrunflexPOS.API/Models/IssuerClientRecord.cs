namespace GrunflexPOS.API.Models;

public sealed class IssuerClientRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string ClientCode { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Business { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Phone { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
