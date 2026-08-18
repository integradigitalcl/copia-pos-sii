using Grunflex.Licensing;

namespace GrunflexPOS.API.Models;

public sealed class LicenseIssuerRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ActivationId { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string BusinessName { get; set; } = string.Empty;
    public string LicenseType { get; set; } = string.Empty;
    public int NumberOfBoxes { get; set; }
    public DateTime ExpUtc { get; set; }
    public bool Multicaja { get; set; }
    public bool OnlineSupport { get; set; }
    public bool CloudBackup { get; set; }
    public bool PrioritySupport { get; set; }
    public int OfflineGraceDays { get; set; } = GrunflexLicenseDefaults.OfflineGraceDays;
    public string LicenseToken { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
