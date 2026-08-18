using PosEdge.Terminal.Offline;

namespace PosEdge.Terminal.TerminalConfig;

public sealed record TerminalAppConfig(
    string ApiBaseUrl,
    Guid TenantId,
    Guid BranchId,
    Guid TerminalId,
    Guid CashSessionId,
    OfflineMode OfflineMode,
    string ReplicaPath);

