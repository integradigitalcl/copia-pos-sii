namespace GrunflexPOS.API.DTOs;

public sealed record TenantBackupFileInfo(string RelativePath, long SizeBytes, DateTime StoredAtUtc);
