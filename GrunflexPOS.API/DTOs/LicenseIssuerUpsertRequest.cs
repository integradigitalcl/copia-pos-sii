using Grunflex.Licensing;

namespace GrunflexPOS.API.DTOs;

public sealed class LicenseIssuerUpsertRequest
{
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
}
