using System.Data;
using Microsoft.EntityFrameworkCore.Storage;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PosEdge.Infrastructure;
using PosEdge.Shared;

namespace PosEdge.Application.Admission;

public sealed record AdmissionClaimCommand(
    Guid TenantId,
    Guid BranchId,
    Guid TerminalId,
    string Kind,               // replay|snapshot
    int TtlSeconds,
    string RequestId) : IRequest<AdmissionClaimResponse>;

public sealed record AdmissionReleaseCommand(
    string Kind,
    string Token) : IRequest<AdmissionReleaseResponse>;

public sealed record AdmissionClaimResponse(bool Ok, string Code, string? Message, string? Token, int? SlotId, int? RetryAfterMs);
public sealed record AdmissionReleaseResponse(bool Ok, string Code, string? Message);

public sealed class AdmissionClaimHandler : IRequestHandler<AdmissionClaimCommand, AdmissionClaimResponse>
{
    private readonly PosEdgeDbContext _db;
    public AdmissionClaimHandler(PosEdgeDbContext db) => _db = db;

    public async Task<AdmissionClaimResponse> Handle(AdmissionClaimCommand cmd, CancellationToken ct)
    {
        var kind = cmd.Kind?.Trim().ToLowerInvariant();
        if (kind is not ("replay" or "snapshot"))
            return new(false, "VALIDATION_ERROR", "kind inválido", null, null, null);
        var ttl = Math.Clamp(cmd.TtlSeconds, 2, 60);

        var token = cmd.RequestId; // deterministic token (ULID) from caller
        var until = DateTimeOffset.UtcNow.AddSeconds(ttl);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Idempotency: if token already claimed and not expired, return same slot.
        var existing = await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE admission_slots
               SET claimed_until = GREATEST(claimed_until, {until})
             WHERE slot_kind = {kind}
               AND claimed_token = {token}
               AND claimed_until >= now();", ct);

        if (existing > 0)
        {
            // Read slotId for this token.
            var slotId = await ReadSlotIdForTokenAsync(kind, token, ct);
            await tx.CommitAsync(ct);
            return new(true, "OK", null, token, slotId, null);
        }

        // Try to claim a free slot.
        var rows = await _db.Database.ExecuteSqlInterpolatedAsync($@"
            WITH picked AS (
              SELECT slot_id
                FROM admission_slots
               WHERE slot_kind = {kind}
                 AND (claimed_until IS NULL OR claimed_until < now())
               ORDER BY slot_id
               LIMIT 1
               FOR UPDATE SKIP LOCKED
            )
            UPDATE admission_slots s
               SET claimed_by = {cmd.TerminalId},
                   claimed_token = {token},
                   claimed_at = now(),
                   claimed_until = {until}
              FROM picked
             WHERE s.slot_kind = {kind}
               AND s.slot_id = picked.slot_id;", ct);

        if (rows == 0)
        {
            await tx.RollbackAsync(ct);
            return new(false, "NO_SLOTS", "no slots disponibles", null, null, 1000 + Random.Shared.Next(0, 500));
        }

        var slotId2 = await ReadSlotIdForTokenAsync(kind, token, ct);

        await tx.CommitAsync(ct);
        return new(true, "OK", null, token, slotId2, null);
    }

    private async Task<int> ReadSlotIdForTokenAsync(string kind, string token, CancellationToken ct)
    {
        // Use the current EF connection/transaction (avoid opening a second connection inside tx).
        await using var cmd = _db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT slot_id FROM admission_slots WHERE slot_kind=@k AND claimed_token=@t LIMIT 1;";

        var p1 = cmd.CreateParameter();
        p1.ParameterName = "@k";
        p1.Value = kind;
        cmd.Parameters.Add(p1);

        var p2 = cmd.CreateParameter();
        p2.ParameterName = "@t";
        p2.Value = token;
        cmd.Parameters.Add(p2);

        var tx = _db.Database.CurrentTransaction;
        if (tx != null)
            cmd.Transaction = tx.GetDbTransaction();

        if (cmd.Connection!.State != ConnectionState.Open)
            await cmd.Connection.OpenAsync(ct);

        var o = await cmd.ExecuteScalarAsync(ct);
        return o == null || o == DBNull.Value ? 0 : Convert.ToInt32(o);
    }
}

public sealed class AdmissionReleaseHandler : IRequestHandler<AdmissionReleaseCommand, AdmissionReleaseResponse>
{
    private readonly PosEdgeDbContext _db;
    public AdmissionReleaseHandler(PosEdgeDbContext db) => _db = db;

    public async Task<AdmissionReleaseResponse> Handle(AdmissionReleaseCommand cmd, CancellationToken ct)
    {
        var kind = cmd.Kind?.Trim().ToLowerInvariant();
        if (kind is not ("replay" or "snapshot"))
            return new(false, "VALIDATION_ERROR", "kind inválido");
        if (string.IsNullOrWhiteSpace(cmd.Token))
            return new(false, "VALIDATION_ERROR", "token requerido");

        var rows = await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE admission_slots
               SET claimed_by = NULL,
                   claimed_token = NULL,
                   claimed_at = NULL,
                   claimed_until = NULL
             WHERE slot_kind = {kind}
               AND claimed_token = {cmd.Token};", ct);

        return rows > 0 ? new(true, "OK", null) : new(false, "NOT_FOUND", "token no encontrado");
    }
}

