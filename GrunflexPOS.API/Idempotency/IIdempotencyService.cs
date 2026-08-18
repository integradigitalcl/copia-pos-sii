namespace GrunflexPOS.API.Idempotency;

public interface IIdempotencyService
{
    string HashPayload<T>(T payload);

    Task<IdempotencyBeginResult> BeginAsync(
        IdempotencyOperationType operationType,
        IdempotencyRequestDescriptor descriptor,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        Guid recordId,
        int responseCode,
        string responsePayloadJson,
        string? resourceType,
        string? resourceId,
        CancellationToken cancellationToken = default);

    Task FailAsync(
        Guid recordId,
        int responseCode,
        string errorPayloadJson,
        CancellationToken cancellationToken = default);
}
