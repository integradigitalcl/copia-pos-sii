namespace GrunflexPOS.API.DTOs;

public sealed class TerminalRegisterRequest
{
    public string MachineFingerprint { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ActivationId { get; set; } = string.Empty;
}

public sealed class TerminalRegisterResponse
{
    public bool Granted { get; set; }
    public string? Reason { get; set; }
    public int SlotsInUse { get; set; }
    public int SlotsTotal { get; set; }
    public System.Guid? TerminalId { get; set; }
}

public sealed class TerminalHeartbeatRequest
{
    public string MachineFingerprint { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? MachineName { get; set; }
}

public sealed class TerminalHeartbeatResponse
{
    public bool Authorized { get; set; }
    public string? Reason { get; set; }
}

public sealed class TerminalListItem
{
    public System.Guid Id { get; set; }
    public string MachineFingerprint { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ActivationId { get; set; } = string.Empty;
    public System.DateTime FirstSeenUtc { get; set; }
    public System.DateTime LastHeartbeatUtc { get; set; }
    public bool Active { get; set; }
    public string? IpAddress { get; set; }
}
