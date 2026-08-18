namespace GrunflexPOS.API.Idempotency;

public enum IdempotencyBeginAction
{
    /// <summary>Nueva operación; ejecutar handler y luego Complete/Fail.</summary>
    ProceedNew,

    /// <summary>Reintento del mismo worker tras InProgress (mismo hash).</summary>
    ProceedResumeInProgress,

    /// <summary>Operación ya completada; usar <see cref="IdempotencyBeginResult.StoredResponsePayload"/>.</summary>
    ReplayCompleted,

    /// <summary>Mismo RequestId con hash distinto — posible replay malicioso o bug cliente.</summary>
    RejectedHashMismatch,

    /// <summary>Otra terminal/proceso está procesando el mismo RequestId.</summary>
    RejectedConcurrent,

    /// <summary>RequestId inválido o faltante.</summary>
    RejectedInvalidRequest
}

public sealed class IdempotencyBeginResult
{
    public IdempotencyBeginAction Action { get; init; }
    public Guid? RecordId { get; init; }
    public int? StoredResponseCode { get; init; }
    public string? StoredResponsePayload { get; init; }
    public string? StoredResourceType { get; init; }
    public string? StoredResourceId { get; init; }
    public string? ErrorMessage { get; init; }

    public static IdempotencyBeginResult Proceed(Guid recordId, bool resume) => new()
    {
        Action = resume ? IdempotencyBeginAction.ProceedResumeInProgress : IdempotencyBeginAction.ProceedNew,
        RecordId = recordId
    };

    public static IdempotencyBeginResult Replay(int code, string payload, string? resourceType, string? resourceId) => new()
    {
        Action = IdempotencyBeginAction.ReplayCompleted,
        StoredResponseCode = code,
        StoredResponsePayload = payload,
        StoredResourceType = resourceType,
        StoredResourceId = resourceId
    };

    public static IdempotencyBeginResult Reject(IdempotencyBeginAction action, string message) => new()
    {
        Action = action,
        ErrorMessage = message
    };
}
