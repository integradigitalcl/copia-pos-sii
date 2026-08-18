namespace GrunflexPOS.API.DTOs;

public sealed class SupportTicketCreateRequest
{
    public string ActivationId { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;
}
