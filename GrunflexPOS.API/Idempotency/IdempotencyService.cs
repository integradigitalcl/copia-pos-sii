using Grunflex.Idempotency;
using GrunflexPOS.API.Data;
using GrunflexPOS.API.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Idempotency;

public sealed class IdempotencyService : IIdempotencyService
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan InProgressStaleAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ConcurrentWindow = TimeSpan.FromMinutes(2);

    private readonly ApiDbContext _db;
    private readonly ILogger<IdempotencyService> _log;

    public IdempotencyService(ApiDbContext db, ILogger<IdempotencyService> log)
    {
        _db = db;
        _log = log;
    }

    public string HashPayload<T>(T payload) => IdempotencyPayloadHasher.HashPayload(payload);

    public async Task<IdempotencyBeginResult> BeginAsync(
        IdempotencyOperationType operationType,
        IdempotencyRequestDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        var op = operationType.ToStorageName();
        var requestId = (descriptor.RequestId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(requestId) || requestId.Length > 64)
        {
            _log.LogWarning("idempotency.invalid_request missing or oversized RequestId");
            return IdempotencyBeginResult.Reject(IdempotencyBeginAction.RejectedInvalidRequest,
                "RequestId inválido (requerido, máx. 64).");
        }

        var hash = (descriptor.RequestHash ?? string.Empty).Trim().ToLowerInvariant();
        if (hash.Length != 64)
        {
            _log.LogWarning("idempotency.invalid_request bad hash requestId={RequestId}", requestId);
            return IdempotencyBeginResult.Reject(IdempotencyBeginAction.RejectedInvalidRequest,
                "RequestHash inválido (SHA-256 hex de 64 caracteres).");
        }

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await IdempotencyAdvisoryLock.AcquireAsync(_db, requestId, op, cancellationToken);

            var existing = await _db.Set<IdempotencyRecord>()
                .Where(x => x.RequestId == requestId && x.OperationType == op)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing != null)
            {
                if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
                {
                    _log.LogWarning(
                        "idempotency.replay_mismatch requestId={RequestId} operation={Operation} terminal={Terminal} caja={CajaId}",
                        requestId, op, descriptor.TerminalId, descriptor.CajaId);
                    await tx.CommitAsync(cancellationToken);
                    return IdempotencyBeginResult.Reject(IdempotencyBeginAction.RejectedHashMismatch,
                        "RequestId ya usado con un payload distinto (hash no coincide).");
                }

                switch (existing.Status)
                {
                    case IdempotencyRecordStatus.Completed:
                        _log.LogInformation(
                            "idempotency.replay_completed requestId={RequestId} operation={Operation} resource={ResourceType}/{ResourceId}",
                            requestId, op, existing.ResourceType, existing.ResourceId);
                        await tx.CommitAsync(cancellationToken);
                        return IdempotencyBeginResult.Replay(
                            existing.ResponseCode ?? StatusCodes.Status200OK,
                            existing.ResponsePayload ?? "{}",
                            existing.ResourceType,
                            existing.ResourceId);

                    case IdempotencyRecordStatus.Failed:
                        _log.LogInformation(
                            "idempotency.replay_failed requestId={RequestId} operation={Operation}",
                            requestId, op);
                        await tx.CommitAsync(cancellationToken);
                        return IdempotencyBeginResult.Replay(
                            existing.ResponseCode ?? StatusCodes.Status409Conflict,
                            existing.ResponsePayload ?? "{}",
                            existing.ResourceType,
                            existing.ResourceId);

                    case IdempotencyRecordStatus.InProgress:
                        var age = DateTime.UtcNow - existing.CreatedAt;
                        if (age < ConcurrentWindow)
                        {
                            _log.LogWarning(
                                "idempotency.concurrent_request requestId={RequestId} operation={Operation} ageSec={Age}",
                                requestId, op, (int)age.TotalSeconds);
                            await tx.CommitAsync(cancellationToken);
                            return IdempotencyBeginResult.Reject(IdempotencyBeginAction.RejectedConcurrent,
                                "La operación ya está en curso en el servidor (reintente en unos segundos).");
                        }

                        if (age >= InProgressStaleAfter)
                        {
                            existing.Status = IdempotencyRecordStatus.Failed;
                            existing.CompletedAt = DateTime.UtcNow;
                            existing.ResponseCode = StatusCodes.Status503ServiceUnavailable;
                            existing.ResponsePayload = """{"error":"STALE_IN_PROGRESS","message":"Operación abandonada; reintente con el mismo RequestId."}""";
                            _log.LogWarning(
                                "idempotency.stale_in_progress requestId={RequestId} operation={Operation}",
                                requestId, op);
                            await _db.SaveChangesAsync(cancellationToken);
                        }
                        else
                        {
                            _log.LogInformation(
                                "idempotency.resume_in_progress requestId={RequestId} operation={Operation}",
                                requestId, op);
                            await tx.CommitAsync(cancellationToken);
                            return IdempotencyBeginResult.Proceed(existing.Id, resume: true);
                        }

                        break;
                }
            }

            var record = new IdempotencyRecord
            {
                Id = Guid.NewGuid(),
                RequestId = requestId,
                OperationType = op,
                RequestHash = hash,
                TerminalId = Truncate(descriptor.TerminalId, 120),
                CajaId = descriptor.CajaId,
                Status = IdempotencyRecordStatus.InProgress,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(DefaultTtl)
            };

            _db.Add(record);
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            _log.LogInformation(
                "idempotency.begin_new recordId={RecordId} requestId={RequestId} operation={Operation} terminal={Terminal}",
                record.Id, requestId, op, record.TerminalId);

            return IdempotencyBeginResult.Proceed(record.Id, resume: false);
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(cancellationToken); } catch { /* */ }
            _log.LogError(ex, "idempotency.begin_error requestId={RequestId}", requestId);
            throw;
        }
    }

    public async Task CompleteAsync(
        Guid recordId,
        int responseCode,
        string responsePayloadJson,
        string? resourceType,
        string? resourceId,
        CancellationToken cancellationToken = default)
    {
        var record = await _db.Set<IdempotencyRecord>().FirstOrDefaultAsync(x => x.Id == recordId, cancellationToken)
                     ?? throw new InvalidOperationException($"IdempotencyRecord {recordId} no encontrado.");

        record.Status = IdempotencyRecordStatus.Completed;
        record.ResponseCode = responseCode;
        record.ResponsePayload = responsePayloadJson;
        record.ResourceType = Truncate(resourceType, 64);
        record.ResourceId = Truncate(resourceId, 64);
        record.CompletedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        _log.LogInformation(
            "idempotency.completed recordId={RecordId} requestId={RequestId} operation={Operation} code={Code} resource={ResourceType}/{ResourceId}",
            record.Id, record.RequestId, record.OperationType, responseCode, record.ResourceType, record.ResourceId);
    }

    public async Task FailAsync(
        Guid recordId,
        int responseCode,
        string errorPayloadJson,
        CancellationToken cancellationToken = default)
    {
        var record = await _db.Set<IdempotencyRecord>().FirstOrDefaultAsync(x => x.Id == recordId, cancellationToken)
                     ?? throw new InvalidOperationException($"IdempotencyRecord {recordId} no encontrado.");

        record.Status = IdempotencyRecordStatus.Failed;
        record.ResponseCode = responseCode;
        record.ResponsePayload = errorPayloadJson;
        record.CompletedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        _log.LogWarning(
            "idempotency.failed recordId={RecordId} requestId={RequestId} operation={Operation} code={Code}",
            record.Id, record.RequestId, record.OperationType, responseCode);
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
}
