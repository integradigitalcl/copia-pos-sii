namespace GrunflexPOS.API.Models;

/// <summary>Evento de auditoría de terminal (registro, activación, desactivación, token inválido).</summary>
public sealed class TerminalAuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TerminalId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Heartbeat enterprise con contexto operacional (separado de licenciamiento).</summary>
public sealed class TerminalHeartbeatRecord
{
    public long Id { get; set; }
    public Guid TerminalId { get; set; }
    public Guid? CajaId { get; set; }
    public Guid? CurrentUserId { get; set; }
    public Guid? CurrentSessionId { get; set; }
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
    public string? ClientVersion { get; set; }
    public string? IpAddress { get; set; }
}

/// <summary>Log incremental de cambios para delta sync multicaja.</summary>
public sealed class MulticajaSyncChangeLog
{
    public long Id { get; set; }
    public string Domain { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public DateTime ChangedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}
