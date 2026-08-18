namespace GrunflexPOS.API.DTOs;

public sealed class IssuerClientResponse
{
    public Guid Id { get; set; }

    public string ClientCode { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Business { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Phone { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public int LinkedLicensesCount { get; set; }
}

public sealed class IssuerClientUpsertRequest
{
    public string Name { get; set; } = string.Empty;

    public string Business { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Phone { get; set; } = string.Empty;
}
