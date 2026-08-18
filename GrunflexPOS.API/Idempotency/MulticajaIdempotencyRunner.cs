using System.Text.Json;
using GrunflexPOS.API.Idempotency;

namespace GrunflexPOS.API.Services;

/// <summary>
/// Orquesta idempotencia enterprise (PostgreSQL/SQLite API DB) alrededor de processors multicaja.
/// </summary>
public sealed class MulticajaIdempotencyRunner
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IIdempotencyService _idempotency;
    private readonly IIdempotencyContextAccessor _contextAccessor;
    private readonly ILogger<MulticajaIdempotencyRunner> _log;

    public MulticajaIdempotencyRunner(
        IIdempotencyService idempotency,
        IIdempotencyContextAccessor contextAccessor,
        ILogger<MulticajaIdempotencyRunner> log)
    {
        _idempotency = idempotency;
        _contextAccessor = contextAccessor;
        _log = log;
    }

    public async Task<TResponse> ExecuteAsync<TRequest, TResponse>(
        IdempotencyOperationType operationType,
        TRequest body,
        Func<TRequest, string> getRequestId,
        Func<TRequest, Guid?> getCajaId,
        Func<CancellationToken, Task<TResponse>> handler,
        Func<TResponse, (bool ok, string? resourceType, string? resourceId)> mapResource,
        CancellationToken cancellationToken = default)
        where TResponse : class
    {
        var requestId = (getRequestId(body) ?? string.Empty).Trim();
        var descriptor = BuildDescriptor(body, requestId, getCajaId(body));
        var begin = await _idempotency.BeginAsync(operationType, descriptor, cancellationToken);

        switch (begin.Action)
        {
            case IdempotencyBeginAction.ReplayCompleted:
                return DeserializeStored<TResponse>(begin.StoredResponsePayload!)
                    ?? throw new InvalidOperationException("Respuesta idempotente corrupta.");

            case IdempotencyBeginAction.RejectedHashMismatch:
            case IdempotencyBeginAction.RejectedConcurrent:
            case IdempotencyBeginAction.RejectedInvalidRequest:
                return CreateRejectedResponse<TResponse>(begin.ErrorMessage ?? "Solicitud rechazada.");
        }

        var recordId = begin.RecordId!.Value;
        try
        {
            var response = await handler(cancellationToken);
            var (ok, resourceType, resourceId) = mapResource(response);
            var json = JsonSerializer.Serialize(response, JsonOpts);
            var code = ok ? StatusCodes.Status200OK : StatusCodes.Status409Conflict;
            await _idempotency.CompleteAsync(recordId, code, json, resourceType, resourceId, cancellationToken);
            return response;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "idempotency.handler_error requestId={RequestId} operation={Op}", requestId, operationType);
            var errJson = JsonSerializer.Serialize(new { error = "ERROR_INTERNO", message = ex.Message }, JsonOpts);
            await _idempotency.FailAsync(recordId, StatusCodes.Status500InternalServerError, errJson, cancellationToken);
            throw;
        }
    }

    private IdempotencyRequestDescriptor BuildDescriptor<TRequest>(
        TRequest body,
        string requestIdFromBody,
        Guid? cajaIdFromBody)
    {
        var fromHeaders = _contextAccessor.Current;
        var hash = _idempotency.HashPayload(body);

        if (fromHeaders != null && !string.IsNullOrWhiteSpace(fromHeaders.RequestId))
        {
            if (!string.Equals(fromHeaders.RequestId, requestIdFromBody, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning(
                    "idempotency.header_body_mismatch header={H} body={B}",
                    fromHeaders.RequestId, requestIdFromBody);
            }

            var headerHash = fromHeaders.RequestHash;
            if (!string.IsNullOrEmpty(headerHash) &&
                !string.Equals(headerHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning("idempotency.header_hash_mismatch requestId={RequestId}", fromHeaders.RequestId);
            }

            return new IdempotencyRequestDescriptor
            {
                RequestId = fromHeaders.RequestId,
                RequestHash = string.IsNullOrEmpty(headerHash) ? hash : headerHash,
                TerminalId = string.IsNullOrWhiteSpace(fromHeaders.TerminalId)
                    ? Environment.MachineName
                    : fromHeaders.TerminalId,
                CajaId = fromHeaders.CajaId ?? cajaIdFromBody
            };
        }

        return new IdempotencyRequestDescriptor
        {
            RequestId = requestIdFromBody,
            RequestHash = hash,
            TerminalId = Environment.MachineName,
            CajaId = cajaIdFromBody
        };
    }

    private static TResponse? DeserializeStored<TResponse>(string json) where TResponse : class =>
        JsonSerializer.Deserialize<TResponse>(json, JsonOpts);

    private static TResponse CreateRejectedResponse<TResponse>(string message) where TResponse : class
    {
        var t = typeof(TResponse);
        var instance = Activator.CreateInstance(t) as TResponse
                       ?? throw new InvalidOperationException($"No se pudo crear {t.Name}");

        var okProp = t.GetProperty("Ok");
        okProp?.SetValue(instance, false);
        var errProp = t.GetProperty("Error");
        errProp?.SetValue(instance, message);
        var codeProp = t.GetProperty("ErrorCode");
        codeProp?.SetValue(instance, "IDEMPOTENCY_REJECTED");

        return instance;
    }
}
