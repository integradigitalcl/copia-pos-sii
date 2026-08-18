namespace GrunflexPOS.API.DTOs;

public sealed class SupportTicketResponse
{
    public Guid Id { get; set; }

    public string ActivationId { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}
