namespace GrunflexPOS.API.Models;

public sealed class SupportTicket
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string ActivationId { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string Status { get; set; } = "Abierto";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
