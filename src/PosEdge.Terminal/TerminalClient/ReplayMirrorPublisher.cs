using Microsoft.Data.Sqlite;
using System.Net.Http.Json;
using PosEdge.Terminal.Replica;
using System.Text.Json;

namespace PosEdge.Terminal.Client;

internal sealed class ReplayMirrorPublisher
{
    private readonly HttpClient _http;
    private readonly TerminalReplicaDb _replica;
    private readonly Guid _tenantId;
    private readonly Guid _branchId;
    private readonly Guid _terminalId;

    public ReplayMirrorPublisher(HttpClient http, TerminalReplicaDb replica, Guid tenantId, Guid branchId, Guid terminalId)
    {
        _http = http;
        _replica = replica;
        _tenantId = tenantId;
        _branchId = branchId;
        _terminalId = terminalId;
    }

    public async Task PublishOnceAsync(CancellationToken ct)
    {
        var snap = ReadSnapshot();
        using var resp = await _http.PostAsJsonAsync("/v1/terminal/replay-mirror", snap, ct);
        resp.EnsureSuccessStatusCode();
    }

    private object ReadSnapshot()
    {
        using var conn = _replica.OpenConnection();
        var items = new List<object>();
        using (var cmd = conn.CreateCommand())
        {
            // Map local schema to ops replay states.
            // local_outbox.state: pending|inflight|dead
            // If pending with next_retry_at_ms != null and > now => retry_wait
            // If dead => dead_letter
            cmd.CommandText = """
                              SELECT request_id, type, state, created_at_ms, inflight_at_ms, attempt_count, last_error, next_retry_at_ms
                                FROM local_outbox;
                              """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var now = TerminalReplicaDb.NowMs();
                var st = r.GetString(2);
                var next = r.IsDBNull(7) ? (long?)null : r.GetInt64(7);
                var mapped = st switch
                {
                    "dead" => "dead_letter",
                    "pending" when next.HasValue && next.Value > now => "retry_wait",
                    _ => st
                };
                items.Add(new
                {
                    requestId = r.GetString(0),
                    type = r.GetString(1),
                    state = mapped,
                    createdAtMs = r.GetInt64(3),
                    inflightAtMs = r.IsDBNull(4) ? (long?)null : r.GetInt64(4),
                    attemptCount = r.GetInt32(5),
                    lastError = r.IsDBNull(6) ? null : r.GetString(6),
                    nextRetryAtMs = next
                });
            }
        }

        var dead = new List<object>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                              SELECT request_id, type, dead_reason, classification, failed_at_ms, retry_count, last_server_code, last_error, payload_snapshot_json
                                FROM dead_letter;
                              """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                dead.Add(new
                {
                    requestId = r.GetString(0),
                    type = r.GetString(1),
                    deadReason = r.GetString(2),
                    classification = r.GetString(3),
                    failedAtMs = r.GetInt64(4),
                    retryCount = r.GetInt32(5),
                    lastServerCode = r.IsDBNull(6) ? null : r.GetString(6),
                    lastError = r.IsDBNull(7) ? null : r.GetString(7),
                    payloadSnapshotJson = r.GetString(8)
                });
            }
        }

        // Tail replay_log since last publish to support ops timeline.
        var lastLogId = ReadLastLogCursor(conn);
        var logs = new List<object>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                              SELECT id, at_ms, kind, request_id, message, meta_json
                                FROM replay_log
                               WHERE id > $last
                               ORDER BY id
                               LIMIT 200;
                              """;
            cmd.Parameters.AddWithValue("$last", lastLogId);
            using var r = cmd.ExecuteReader();
            long maxId = lastLogId;
            while (r.Read())
            {
                var id = r.GetInt64(0);
                if (id > maxId) maxId = id;
                logs.Add(new
                {
                    atMs = r.GetInt64(1),
                    kind = r.GetString(2),
                    requestId = r.IsDBNull(3) ? null : r.GetString(3),
                    message = r.GetString(4),
                    metaJson = r.IsDBNull(5) ? null : r.GetString(5)
                });
            }
            if (maxId != lastLogId)
                WriteLastLogCursor(conn, maxId);
        }

        var health = ReadTerminalHealth(conn);

        return new
        {
            tenantId = _tenantId,
            branchId = _branchId,
            terminalId = _terminalId,
            atMs = TerminalReplicaDb.NowMs(),
            items,
            deadLetters = dead,
            replayLog = logs,
            terminalHealth = health
        };
    }

    private object? ReadTerminalHealth(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                              SELECT state, updated_at_ms, last_server_seq, last_applied_seq, last_error, meta_json
                                FROM terminal_health
                               WHERE tenant_id=$t AND branch_id=$b AND terminal_id=$term
                               LIMIT 1;
                              """;
            cmd.Parameters.AddWithValue("$t", _tenantId.ToString("D"));
            cmd.Parameters.AddWithValue("$b", _branchId.ToString("D"));
            cmd.Parameters.AddWithValue("$term", _terminalId.ToString("D"));
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;

            var st = r.GetString(0);
            var updatedAtMs = r.GetInt64(1);
            var lastServerSeq = r.IsDBNull(2) ? (long?)null : r.GetInt64(2);
            var lastAppliedSeq = r.IsDBNull(3) ? (long?)null : r.GetInt64(3);
            var lastErr = r.IsDBNull(4) ? null : r.GetString(4);
            var metaJson = r.IsDBNull(5) ? null : r.GetString(5);

            string? degradedReason = null;
            long? lastReplayAtMs = null;
            long? lastSnapshotAtMs = null;
            long? lastSyncOkAtMs = null;
            string? version = null;
            string? build = null;
            if (!string.IsNullOrWhiteSpace(metaJson))
            {
                try
                {
                    var root = JsonSerializer.Deserialize<JsonElement>(metaJson);
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        if (root.TryGetProperty("reason", out var rr) && rr.ValueKind == JsonValueKind.String)
                            degradedReason = rr.GetString();
                        if (root.TryGetProperty("lastReplayAtMs", out var lra) && lra.ValueKind == JsonValueKind.Number)
                            lastReplayAtMs = lra.GetInt64();
                        if (root.TryGetProperty("lastSnapshotAtMs", out var lsa) && lsa.ValueKind == JsonValueKind.Number)
                            lastSnapshotAtMs = lsa.GetInt64();
                        if (root.TryGetProperty("lastSyncOkAtMs", out var lso) && lso.ValueKind == JsonValueKind.Number)
                            lastSyncOkAtMs = lso.GetInt64();
                        if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                            version = v.GetString();
                        if (root.TryGetProperty("build", out var b) && b.ValueKind == JsonValueKind.String)
                            build = b.GetString();
                    }
                }
                catch { }
            }

            Guid? activeLeaseId = null;
            long? leaseExpiresAtMs = null;
            try
            {
                using var cmd2 = conn.CreateCommand();
                cmd2.CommandText = """
                                   SELECT lease_id, expires_at_ms
                                     FROM leases
                                    WHERE status='active'
                                      AND expires_at_ms > $now
                                    ORDER BY expires_at_ms DESC
                                    LIMIT 1;
                                   """;
                cmd2.Parameters.AddWithValue("$now", TerminalReplicaDb.NowMs());
                using var r2 = cmd2.ExecuteReader();
                if (r2.Read())
                {
                    var lid = r2.GetString(0);
                    if (Guid.TryParse(lid, out var g))
                        activeLeaseId = g;
                    leaseExpiresAtMs = r2.GetInt64(1);
                }
            }
            catch { }

            int? localQueueDepth = null;
            try
            {
                using var cmd3 = conn.CreateCommand();
                cmd3.CommandText = "SELECT COUNT(*) FROM local_outbox WHERE state IN ('pending','inflight');";
                var o = cmd3.ExecuteScalar();
                localQueueDepth = o == null ? null : Convert.ToInt32(o);
            }
            catch { }

            return new
            {
                state = st,
                updatedAtMs,
                lastAppliedSeq,
                lastServerSeq,
                lastError = lastErr,
                activeLeaseId,
                leaseExpiresAtMs,
                localQueueDepth,
                lastReplayAtMs,
                lastSnapshotAtMs,
                lastSyncOkAtMs,
                version,
                build,
                degradedReason
            };
        }
        catch
        {
            return null;
        }
    }

    private static long ReadLastLogCursor(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                              CREATE TABLE IF NOT EXISTS mirror_cursor (
                                key TEXT PRIMARY KEY,
                                value INTEGER NOT NULL
                              );
                              SELECT value FROM mirror_cursor WHERE key='replay_log_last_id' LIMIT 1;
                              """;
            var o = cmd.ExecuteScalar();
            return o == null ? 0 : Convert.ToInt64(o);
        }
        catch { return 0; }
    }

    private static void WriteLastLogCursor(SqliteConnection conn, long id)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                              INSERT INTO mirror_cursor(key, value) VALUES ('replay_log_last_id', $v)
                              ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                              """;
            cmd.Parameters.AddWithValue("$v", id);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }
}

