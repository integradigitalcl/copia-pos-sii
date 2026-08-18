using MessagePack;

namespace PosEdge.Contracts.Protocol;

/// <summary>
/// Logical envelope independent of transport (WS/HTTP/TCP).
/// Use MessagePack for WS; JSON for HTTP fallback.
/// </summary>
[MessagePackObject(true)]
public sealed class McEnvelope
{
    /// <summary>Protocol version.</summary>
    public int V { get; set; } = 1;

    /// <summary>Message type name (e.g. "Sale.Commit").</summary>
    public string T { get; set; } = "";

    /// <summary>Message/frame id (ULID string).</summary>
    public string Mid { get; set; } = "";

    /// <summary>Correlation id (ULID string) for req/resp pairing.</summary>
    public string Cid { get; set; } = "";

    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid TerminalId { get; set; }

    /// <summary>Optional token if you choose token-based auth in addition to mTLS.</summary>
    public string? SessionToken { get; set; }

    /// <summary>Client-supplied timestamp for diagnostics only (ms since epoch).</summary>
    public long SentAt { get; set; }

    /// <summary>Payload encoded as MessagePack object (dynamic by T).</summary>
    public object? Body { get; set; }
}

