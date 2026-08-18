namespace GrunflexPOS.API.DTOs;

public sealed class IssuerActivationResponse
{
    public Guid Id { get; set; }

    public string ActivationId { get; set; } = string.Empty;

    public string CustomerDisplay { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string HardwareId { get; set; } = string.Empty;

    public DateTime ActivatedAtUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }

    public string Status { get; set; } = string.Empty;
}

public sealed class IssuerActivationUpsertRequest
{
    public string ActivationId { get; set; } = string.Empty;

    public string CustomerDisplay { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string HardwareId { get; set; } = string.Empty;

    public DateTime? ActivatedAtUtc { get; set; }

    public DateTime? LastSeenUtc { get; set; }

    public string Status { get; set; } = "Activa";
}
