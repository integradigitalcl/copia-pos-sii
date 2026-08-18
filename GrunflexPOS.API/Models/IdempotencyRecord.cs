namespace GrunflexPOS.API.Models;

public sealed class IdempotencyRecord
{
    public Guid Id { get; set; }

    /// <summary>Clave de idempotencia del cliente (GUID sin guiones recomendado).</summary>
    public string RequestId { get; set; } = string.Empty;

    public string OperationType { get; set; } = string.Empty;

    /// <summary>SHA-256 hex del payload canónico.</summary>
    public string RequestHash { get; set; } = string.Empty;

    public string TerminalId { get; set; } = string.Empty;

    public Guid? CajaId { get; set; }

    public IdempotencyRecordStatus Status { get; set; }

    public int? ResponseCode { get; set; }

    /// <summary>JSON serializado de la respuesta API (éxito o error de negocio).</summary>
    public string? ResponsePayload { get; set; }

    public string? ResourceType { get; set; }

    public string? ResourceId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime ExpiresAt { get; set; }
}

public enum IdempotencyRecordStatus
{
    InProgress = 0,
    Completed = 1,
    Failed = 2
}
