namespace GrunflexPOS.API.DTOs;

public sealed class BackupUploadResponse
{
    public Guid Id { get; set; }
    public long SizeBytes { get; set; }
    public string Sha256Hex { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class BackupListItemResponse
{
    public Guid Id { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256Hex { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
