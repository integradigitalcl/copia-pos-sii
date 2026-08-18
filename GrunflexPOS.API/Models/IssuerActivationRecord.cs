namespace GrunflexPOS.API.Models;

public sealed class IssuerActivationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Identificador de activación de la licencia (ej. GF-20260509-1379).</summary>
    public string ActivationId { get; set; } = string.Empty;

    public string CustomerDisplay { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string HardwareId { get; set; } = string.Empty;

    public DateTime ActivatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Activa, Suspendida, Revocada.</summary>
    public string Status { get; set; } = "Activa";
}
