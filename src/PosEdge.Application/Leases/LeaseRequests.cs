using MediatR;

namespace PosEdge.Application.Leases;

public sealed record LeaseRequestCommand(
    Guid TenantId,
    Guid BranchId,
    Guid TerminalId,
    string RequestId,
    int TtlSeconds,
    IReadOnlyList<LeaseRequestLine> Lines) : IRequest<LeaseResponse>;

public sealed record LeaseRenewCommand(
    Guid TenantId,
    Guid BranchId,
    Guid TerminalId,
    Guid LeaseId,
    string RequestId,
    int ExtendSeconds) : IRequest<LeaseResponse>;

public sealed record LeaseRevokeCommand(
    Guid TenantId,
    Guid BranchId,
    Guid LeaseId,
    Guid? TerminalId,
    string RequestId,
    string Reason,
    string? Note) : IRequest<LeaseRevokeResponse>;

public sealed record LeaseRequestLine(Guid ProductId, decimal Qty);

public sealed record LeaseResponse(
    bool Ok,
    string Code,
    string? Message,
    Guid? LeaseId,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<LeaseLineDto>? Lines);

public sealed record LeaseLineDto(Guid ProductId, decimal QtyAllocated, decimal QtyUsed);

public sealed record LeaseRevokeResponse(
    bool Ok,
    string Code,
    string? Message,
    Guid? LeaseId,
    long? EventSeq);

