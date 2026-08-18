namespace GrunflexPOS.API.DTOs;

public sealed class LicensingActivateRequest
{
    public string ActivationId { get; set; } = string.Empty;

    public string MachineName { get; set; } = string.Empty;
}
