namespace GrunflexPOS.API.DTOs;

public sealed class TerminalEnterpriseRegisterRequest
{
    public Guid InstallationId { get; set; }
    public string TerminalToken { get; set; } = string.Empty;
    public Guid? CajaId { get; set; }
    public Guid? BranchId { get; set; }
    public string? DisplayName { get; set; }
    public string MachineFingerprint { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ActivationId { get; set; } = string.Empty;
}

public sealed class TerminalEnterpriseRegisterResponse
{
    public bool Granted { get; set; }
    public string? Reason { get; set; }
    public int SlotsInUse { get; set; }
    public int SlotsTotal { get; set; }
    public Guid? TerminalId { get; set; }
    public Guid InstallationId { get; set; }
}

public sealed class TerminalEnterpriseHeartbeatRequest
{
    public Guid TerminalId { get; set; }
    public Guid InstallationId { get; set; }
    public string TerminalToken { get; set; } = string.Empty;
    public Guid? CajaId { get; set; }
    public Guid? CurrentUserId { get; set; }
    public Guid? CurrentSessionId { get; set; }
    public string Version { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool Reconnect { get; set; }
}

public sealed class TerminalEnterpriseHeartbeatResponse
{
    public bool Authorized { get; set; }
    public string? Reason { get; set; }
    public DateTime ServerTimeUtc { get; set; } = DateTime.UtcNow;
    public bool TerminalActive { get; set; }
}

public sealed class TerminalValidateRequest
{
    public Guid TerminalId { get; set; }
    public Guid InstallationId { get; set; }
    public string TerminalToken { get; set; } = string.Empty;
}

public sealed class TerminalValidateResponse
{
    public bool Valid { get; set; }
    public string? Reason { get; set; }
    public bool Active { get; set; }
    public Guid? CajaId { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
}

public sealed class SyncChangesResponse
{
    public long NextCursor { get; set; }
    public DateTime ServerTimeUtc { get; set; } = DateTime.UtcNow;
    public IReadOnlyList<SyncChangeItemDto> Changes { get; set; } = Array.Empty<SyncChangeItemDto>();
    public IReadOnlyList<string> DeletedIds { get; set; } = Array.Empty<string>();
    public bool ResetRequired { get; set; }
}

public sealed class SyncChangeItemDto
{
    public long Cursor { get; set; }
    public string Domain { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public DateTime ChangedAtUtc { get; set; }
}
