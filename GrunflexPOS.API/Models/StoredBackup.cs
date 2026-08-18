namespace GrunflexPOS.API.Models;

public sealed class StoredBackup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string ActivationId { get; set; } = string.Empty;

    public string OriginalFileName { get; set; } = string.Empty;

    public string StorageFileName { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string Sha256Hex { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
