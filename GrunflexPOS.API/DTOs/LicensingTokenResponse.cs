namespace GrunflexPOS.API.DTOs;

public sealed class LicensingTokenResponse
{
    public string LicenseToken { get; set; } = string.Empty;

    public DateTime ExpUtc { get; set; }

    public bool Multicaja { get; set; }

    public bool OnlineSupport { get; set; }

    public bool CloudBackup { get; set; }

    public bool PrioritySupport { get; set; }

    public string ActivationId { get; set; } = string.Empty;

    public DateTime LastValidUtc { get; set; } = DateTime.UtcNow;

    public int OfflineGraceDays { get; set; }

    public int NumberOfBoxes { get; set; }
}
