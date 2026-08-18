namespace GrunflexPOS.API.DTOs;

public sealed class LicensingStatusResponse
{
    public bool Found { get; set; }

    public bool Expired { get; set; }

    public DateTime ExpUtc { get; set; }

    public bool Multicaja { get; set; }

    public bool OnlineSupport { get; set; }

    public bool CloudBackup { get; set; }

    public bool PrioritySupport { get; set; }

    public int OfflineGraceDays { get; set; }

    public int NumberOfBoxes { get; set; }
}
