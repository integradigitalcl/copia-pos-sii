namespace GrunflexPOS.API.Idempotency;

public sealed class IdempotencyRequestDescriptor
{
    public string RequestId { get; init; } = string.Empty;
    public string RequestHash { get; init; } = string.Empty;
    public string TerminalId { get; init; } = string.Empty;
    public Guid? CajaId { get; init; }
}
