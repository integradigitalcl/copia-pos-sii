using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PosEdge.Terminal.Replica;

/// <summary>
/// Applies server events in strict seq order with dedup + gap detection.
/// Stores pending out-of-order events and drains them when contiguous.
/// </summary>
public sealed class EventApplyEngine
{
    private readonly TerminalReplicaDb _db;
    private readonly Guid _tenantId;
    private readonly Guid _branchId;
    private readonly Guid _terminalId;

    public EventApplyEngine(TerminalReplicaDb db, Guid tenantId, Guid branchId, Guid terminalId)
    {
        _db = db;
        _tenantId = tenantId;
        _branchId = branchId;
        _terminalId = terminalId;
        _db.EnsureCheckpoint(_tenantId, _branchId, _terminalId);
    }

    public long LastAppliedSeq => _db.GetLastAppliedSeq(_tenantId, _branchId, _terminalId);

    public ApplyResult IngestEvent(long seq, string type, DateTimeOffset at, string payloadJson)
    {
        // Store into pending_events (dedup by seq primary key).
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        if (IsAlreadyApplied(conn, tx, seq))
            return ApplyResult.Duplicate(seq);

        InsertPending(conn, tx, seq, type, at, payloadJson);

        var applied = DrainContiguous(conn, tx);
        tx.Commit();
        return applied;
    }

    public ApplyResult ApplyBatch(IEnumerable<ServerEventRow> rows)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        foreach (var r in rows.OrderBy(r => r.Seq))
        {
            if (IsAlreadyApplied(conn, tx, r.Seq))
                continue;
            InsertPending(conn, tx, r.Seq, r.Type, r.At, r.DataJson);
        }

        var applied = DrainContiguous(conn, tx);
        tx.Commit();
        return applied;
    }

    private static bool IsAlreadyApplied(SqliteConnection conn, SqliteTransaction tx, long seq)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM applied_events WHERE seq=$seq LIMIT 1;";
        cmd.Parameters.AddWithValue("$seq", seq);
        return cmd.ExecuteScalar() != null;
    }

    private static void InsertPending(SqliteConnection conn, SqliteTransaction tx, long seq, string type, DateTimeOffset at, string payloadJson)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
                          INSERT OR IGNORE INTO pending_events(seq, type, at_ms, payload_json, received_at_ms)
                          VALUES ($seq, $type, $at, $payload, $now);
                          """;
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$payload", payloadJson);
        cmd.Parameters.AddWithValue("$now", TerminalReplicaDb.NowMs());
        cmd.ExecuteNonQuery();
    }

    private ApplyResult DrainContiguous(SqliteConnection conn, SqliteTransaction tx)
    {
        var start = GetCheckpoint(conn, tx);
        long expected = start + 1;
        var appliedCount = 0;
        long lastApplied = start;

        while (true)
        {
            var pending = GetPending(conn, tx, expected);
            if (pending == null)
                break;

            ApplyOne(conn, tx, pending.Value.Seq, pending.Value.Type, pending.Value.AtMs, pending.Value.PayloadJson);
            DeletePending(conn, tx, expected);
            lastApplied = expected;
            expected++;
            appliedCount++;
        }

        if (appliedCount > 0)
            SetCheckpoint(conn, tx, lastApplied);

        // Gap detection: if there are pending events above expected, we have a gap.
        var minPendingSeq = GetMinPendingSeq(conn, tx);
        if (minPendingSeq.HasValue && minPendingSeq.Value > expected)
            return ApplyResult.Gap(expected, minPendingSeq.Value, appliedCount, lastApplied);

        return ApplyResult.Applied(appliedCount, lastApplied);
    }

    private long GetCheckpoint(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
                          SELECT last_applied_seq
                            FROM sync_checkpoint
                           WHERE tenant_id=$t AND branch_id=$b AND terminal_id=$term;
                          """;
        cmd.Parameters.AddWithValue("$t", _tenantId.ToString("D"));
        cmd.Parameters.AddWithValue("$b", _branchId.ToString("D"));
        cmd.Parameters.AddWithValue("$term", _terminalId.ToString("D"));
        var o = cmd.ExecuteScalar();
        return o == null ? 0 : Convert.ToInt64(o);
    }

    private void SetCheckpoint(SqliteConnection conn, SqliteTransaction tx, long seq)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
                          UPDATE sync_checkpoint
                             SET last_applied_seq=$seq,
                                 updated_at_ms=$now
                           WHERE tenant_id=$t AND branch_id=$b AND terminal_id=$term;
                          """;
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$now", TerminalReplicaDb.NowMs());
        cmd.Parameters.AddWithValue("$t", _tenantId.ToString("D"));
        cmd.Parameters.AddWithValue("$b", _branchId.ToString("D"));
        cmd.Parameters.AddWithValue("$term", _terminalId.ToString("D"));
        cmd.ExecuteNonQuery();
    }

    private static (long Seq, string Type, long AtMs, string PayloadJson)? GetPending(SqliteConnection conn, SqliteTransaction tx, long seq)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT seq, type, at_ms, payload_json FROM pending_events WHERE seq=$seq LIMIT 1;";
        cmd.Parameters.AddWithValue("$seq", seq);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3));
    }

    private static void DeletePending(SqliteConnection conn, SqliteTransaction tx, long seq)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM pending_events WHERE seq=$seq;";
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.ExecuteNonQuery();
    }

    private static long? GetMinPendingSeq(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT MIN(seq) FROM pending_events;";
        var o = cmd.ExecuteScalar();
        if (o == null || o is DBNull) return null;
        return Convert.ToInt64(o);
    }

    private void ApplyOne(SqliteConnection conn, SqliteTransaction tx, long seq, string type, long atMs, string payloadJson)
    {
        // Persist applied event first (durable audit + dedup by PK).
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                              INSERT INTO applied_events(seq, type, at_ms, payload_json, applied_at_ms)
                              VALUES ($seq, $type, $at, $payload, $applied);
                              """;
            cmd.Parameters.AddWithValue("$seq", seq);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$at", atMs);
            cmd.Parameters.AddWithValue("$payload", payloadJson);
            cmd.Parameters.AddWithValue("$applied", TerminalReplicaDb.NowMs());
            cmd.ExecuteNonQuery();
        }

        // Apply to caches.
        if (type == "Sale.Committed")
            ApplySaleCommittedToInventoryCache(conn, tx, payloadJson);
        if (type == "Lease.Revoked")
            ApplyLeaseRevoked(conn, tx, payloadJson);
    }

    private static void ApplyLeaseRevoked(SqliteConnection conn, SqliteTransaction tx, string payloadJson)
    {
        // Expected payload: { type, ..., data: { leaseId, terminalId, reason } }
        JsonElement root;
        try { root = JsonSerializer.Deserialize<JsonElement>(payloadJson); }
        catch { return; }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return;
        if (!data.TryGetProperty("leaseId", out var lidEl)) return;
        var leaseId = lidEl.ValueKind == JsonValueKind.String ? lidEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(leaseId)) return;

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE leases SET status='revoked' WHERE lease_id=$lid;";
            cmd.Parameters.AddWithValue("$lid", leaseId);
            cmd.ExecuteNonQuery();
        }

        // NOTE: moving to dead_letter and blocking sales happens in TerminalClient,
        // because it needs awareness of active lease / mode and local_outbox.
    }

    private static void ApplySaleCommittedToInventoryCache(SqliteConnection conn, SqliteTransaction tx, string payloadJson)
    {
        // Expected payload: { type, tenantId, branchId, seq, at, data: { inventory: [{productId,onHand,reserved}], ... } }
        JsonElement root;
        try { root = JsonSerializer.Deserialize<JsonElement>(payloadJson); }
        catch { return; }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return;
        if (!data.TryGetProperty("inventory", out var inv) || inv.ValueKind != JsonValueKind.Array)
            return;

        foreach (var row in inv.EnumerateArray())
        {
            if (!row.TryGetProperty("productId", out var pidEl)) continue;
            var pid = pidEl.GetString();
            if (string.IsNullOrWhiteSpace(pid)) continue;
            var onHand = row.TryGetProperty("onHand", out var ohEl) ? ohEl.GetDouble() : 0.0;
            var reserved = row.TryGetProperty("reserved", out var rEl) ? rEl.GetDouble() : 0.0;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                              INSERT INTO inventory_cache(product_id, on_hand, reserved, updated_at_ms)
                              VALUES ($id, $on, $res, $now)
                              ON CONFLICT(product_id) DO UPDATE SET
                                on_hand=excluded.on_hand,
                                reserved=excluded.reserved,
                                updated_at_ms=excluded.updated_at_ms;
                              """;
            cmd.Parameters.AddWithValue("$id", pid);
            cmd.Parameters.AddWithValue("$on", onHand);
            cmd.Parameters.AddWithValue("$res", reserved);
            cmd.Parameters.AddWithValue("$now", TerminalReplicaDb.NowMs());
            cmd.ExecuteNonQuery();
        }
    }
}

public sealed record ServerEventRow(long Seq, string Type, DateTimeOffset At, string DataJson);

public sealed record ApplyResult(
    string Kind,
    int AppliedCount,
    long LastAppliedSeq,
    long? GapExpectedSeq,
    long? GapFirstSeenSeq)
{
    public static ApplyResult Applied(int count, long last) => new("applied", count, last, null, null);
    public static ApplyResult Duplicate(long seq) => new("duplicate", 0, seq, null, null);
    public static ApplyResult Gap(long expected, long firstSeen, int count, long lastApplied) =>
        new("gap", count, lastApplied, expected, firstSeen);
}

