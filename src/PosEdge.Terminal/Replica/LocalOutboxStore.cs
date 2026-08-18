using System.Text.Json;
using Microsoft.Data.Sqlite;
using PosEdge.Terminal.Offline;

namespace PosEdge.Terminal.Replica;

public sealed class LocalOutboxStore
{
    private readonly TerminalReplicaDb _db;
    private readonly Guid _tenantId;
    private readonly Guid _branchId;
    private readonly Guid _terminalId;

    public LocalOutboxStore(TerminalReplicaDb db, Guid tenantId, Guid branchId, Guid terminalId)
    {
        _db = db;
        _tenantId = tenantId;
        _branchId = branchId;
        _terminalId = terminalId;
        _db.EnsureCheckpoint(_tenantId, _branchId, _terminalId);
    }

    public void EnqueueSaleCommit(string requestId, OfflineMode mode, string payloadJson, string? leaseId)
    {
        _db.WithWriteRetry(conn =>
        {
            using var tx = conn.BeginTransaction();

            // Idempotency guard: if request already exists in local_outbox/pending_sales, do nothing.
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT 1 FROM local_outbox WHERE request_id=$rid LIMIT 1;";
                cmd.Parameters.AddWithValue("$rid", requestId);
                if (cmd.ExecuteScalar() != null)
                {
                    AppendReplayLog(conn, tx, "enqueue.duplicate", requestId, "Duplicate enqueue ignored");
                    tx.Commit();
                    return;
                }
            }

            if (mode == OfflineMode.LeasesOnly)
            {
                if (string.IsNullOrWhiteSpace(leaseId))
                    throw new InvalidOperationException("LEASES_ONLY requiere leaseId.");

                // En LEASES_ONLY el consumo local debe ser atómico con el enqueue.
                ConsumeLeaseOrThrow(conn, tx, leaseId!, payloadJson);
            }

            // local_outbox
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                                  INSERT OR IGNORE INTO local_outbox(request_id, type, payload_json, created_at_ms, state, attempt_count)
                                  VALUES ($rid, 'Sale.Commit', $payload, $now, 'pending', 0);
                                  """;
                cmd.Parameters.AddWithValue("$rid", requestId);
                cmd.Parameters.AddWithValue("$payload", payloadJson);
                cmd.Parameters.AddWithValue("$now", TerminalReplicaDb.NowMs());
                cmd.ExecuteNonQuery();
            }

            // pending_sales
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                                  INSERT OR IGNORE INTO pending_sales(request_id, created_at_ms, offline_mode, lease_id, status)
                                  VALUES ($rid, $now, $mode, $lease, 'pending');
                                  """;
                cmd.Parameters.AddWithValue("$rid", requestId);
                cmd.Parameters.AddWithValue("$now", TerminalReplicaDb.NowMs());
                cmd.Parameters.AddWithValue("$mode", mode switch
                {
                    OfflineMode.Strict => "strict",
                    OfflineMode.LeasesOnly => "leases_only",
                    OfflineMode.Permissive => "permissive",
                    _ => "strict"
                });
                cmd.Parameters.AddWithValue("$lease", (object?)leaseId ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            // replay log
            AppendReplayLog(conn, tx, "enqueue", requestId, "Enqueued Sale.Commit", new { mode = mode.ToString(), leaseId });

            tx.Commit();
        }, op: "enqueue_sale_commit");
    }

    public void EnqueueCommand(string requestId, string type, OfflineMode mode, string payloadJson, string? leaseId)
    {
        // Generic enqueue for burn-in financial ops. For now, only Sale.Commit consumes leases locally.
        _db.WithWriteRetry(conn =>
        {
            using var tx = conn.BeginTransaction();

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT 1 FROM local_outbox WHERE request_id=$rid LIMIT 1;";
                cmd.Parameters.AddWithValue("$rid", requestId);
                if (cmd.ExecuteScalar() != null)
                {
                    AppendReplayLog(conn, tx, "enqueue.duplicate", requestId, "Duplicate enqueue ignored");
                    tx.Commit();
                    return;
                }
            }

            if (string.Equals(type, "Sale.Commit", StringComparison.OrdinalIgnoreCase) && mode == OfflineMode.LeasesOnly)
            {
                if (string.IsNullOrWhiteSpace(leaseId))
                    throw new InvalidOperationException("LEASES_ONLY requiere leaseId.");
                ConsumeLeaseOrThrow(conn, tx, leaseId!, payloadJson);
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                                  INSERT OR IGNORE INTO local_outbox(request_id, type, payload_json, created_at_ms, state, attempt_count)
                                  VALUES ($rid, $type, $payload, $now, 'pending', 0);
                                  """;
                cmd.Parameters.AddWithValue("$rid", requestId);
                cmd.Parameters.AddWithValue("$type", type);
                cmd.Parameters.AddWithValue("$payload", payloadJson);
                cmd.Parameters.AddWithValue("$now", TerminalReplicaDb.NowMs());
                cmd.ExecuteNonQuery();
            }

            AppendReplayLog(conn, tx, "enqueue", requestId, $"Enqueued {type}", new { mode = mode.ToString(), leaseId });
            tx.Commit();
        }, op: "enqueue_command");
    }

    private static void ConsumeLeaseOrThrow(SqliteConnection conn, SqliteTransaction tx, string leaseId, string saleCommitPayloadJson)
    {
        // Payload is the HTTP body JSON: contains lines: [{productId, qty, ...}]
        JsonElement root;
        try { root = JsonSerializer.Deserialize<JsonElement>(saleCommitPayloadJson); }
        catch { throw new InvalidOperationException("payload_json inválido"); }

        if (!root.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("payload_json sin lines");

        // Aggregate qty by productId
        var qtyByProduct = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.EnumerateArray())
        {
            var pid = l.GetProperty("productId").GetString();
            var qty = l.GetProperty("qty").GetDouble();
            if (string.IsNullOrWhiteSpace(pid) || qty <= 0) continue;
            qtyByProduct[pid] = qtyByProduct.TryGetValue(pid, out var cur) ? cur + qty : qty;
        }
        if (qtyByProduct.Count == 0)
            throw new InvalidOperationException("lines vacías/qty inválida");

        // Check lease exists and is active + not expired
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                              SELECT status, expires_at_ms
                                FROM leases
                               WHERE lease_id=$lease
                               LIMIT 1;
                              """;
            cmd.Parameters.AddWithValue("$lease", leaseId);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
                throw new InvalidOperationException("lease local no existe");
            var status = r.GetString(0);
            var expires = r.GetInt64(1);
            if (!string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"lease local status={status}");
            if (expires <= TerminalReplicaDb.NowMs())
                throw new InvalidOperationException("lease local expirado");
        }

        // Validate remaining and consume: qty_used += qty
        foreach (var (pid, qty) in qtyByProduct)
        {
            // Read allocated/used
            double allocated;
            double used;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                                  SELECT qty_allocated, qty_used
                                    FROM lease_lines
                                   WHERE lease_id=$lease AND product_id=$pid
                                   LIMIT 1;
                                  """;
                cmd.Parameters.AddWithValue("$lease", leaseId);
                cmd.Parameters.AddWithValue("$pid", pid);
                using var r = cmd.ExecuteReader();
                if (!r.Read())
                    throw new InvalidOperationException($"lease sin producto {pid}");
                allocated = r.GetDouble(0);
                used = r.GetDouble(1);
            }
            var remaining = allocated - used;
            if (remaining + 1e-9 < qty)
                throw new InvalidOperationException($"lease exhausted {pid} remaining={remaining} need={qty}");

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                                  UPDATE lease_lines
                                     SET qty_used = qty_used + $qty
                                   WHERE lease_id=$lease AND product_id=$pid
                                     AND (qty_allocated - qty_used) >= $qty;
                                  """;
                cmd.Parameters.AddWithValue("$lease", leaseId);
                cmd.Parameters.AddWithValue("$pid", pid);
                cmd.Parameters.AddWithValue("$qty", qty);
                var rows = cmd.ExecuteNonQuery();
                if (rows == 0)
                    throw new InvalidOperationException($"lease exhausted (race) {pid}");
            }
        }

        AppendReplayLog(conn, tx, "lease.consume", null, "Consumed lease locally", new { leaseId, items = qtyByProduct });
    }

    public List<LocalOutboxItem> DequeueBatch(int max, long nowMs)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          SELECT request_id, type, payload_json, attempt_count
                            FROM local_outbox
                           WHERE state='pending'
                             AND (next_retry_at_ms IS NULL OR next_retry_at_ms <= $now)
                           ORDER BY created_at_ms
                           LIMIT $max;
                          """;
        cmd.Parameters.AddWithValue("$now", nowMs);
        cmd.Parameters.AddWithValue("$max", max);
        var list = new List<LocalOutboxItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new LocalOutboxItem(
                r.GetString(0),
                r.GetString(1),
                r.GetString(2),
                r.GetInt32(3)));
        }
        return list;
    }

    public void MarkInFlight(string requestId)
    {
        _db.WithWriteRetry(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE local_outbox SET state='inflight', inflight_at_ms=$now WHERE request_id=$rid;";
            cmd.Parameters.AddWithValue("$rid", requestId);
            cmd.Parameters.AddWithValue("$now", TerminalReplicaDb.NowMs());
            cmd.ExecuteNonQuery();
        }, op: "mark_inflight");
    }

    public int RecoverStuckInFlight(long cutoffMs)
    {
        // Crash-safety: if process dies after MarkInFlight but before MarkRejected/MarkCommitted,
        // we must return inflight items back to pending so replay can continue on restart.
        var recovered = 0;
        _db.WithWriteRetry(conn =>
        {
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                                  UPDATE local_outbox
                                     SET state='pending',
                                         next_retry_at_ms=NULL,
                                         inflight_at_ms=NULL
                                   WHERE state='inflight'
                                     AND (inflight_at_ms IS NULL OR inflight_at_ms <= $cutoff);
                                  """;
                cmd.Parameters.AddWithValue("$cutoff", cutoffMs);
                recovered = cmd.ExecuteNonQuery();
            }
            if (recovered > 0)
                AppendReplayLog(conn, tx, "inflight_timeout_recovered", null, "Recovered inflight items", new { cutoff = cutoffMs, recovered });
            tx.Commit();
        }, op: "recover_inflight");
        return recovered;
    }

    public void MarkCommitted(string requestId)
    {
        _db.WithWriteRetry(conn =>
        {
            using var tx = conn.BeginTransaction();

            // Record applied command (dedup aid, for burn-in validation).
            try
            {
                using var cmdA = conn.CreateCommand();
                cmdA.Transaction = tx;
                cmdA.CommandText = """
                                   INSERT OR IGNORE INTO applied_commands(request_id, type, at_ms, server_code, server_ref_id)
                                   SELECT request_id, type, $now, 'OK', NULL
                                     FROM local_outbox
                                    WHERE request_id=$rid
                                    LIMIT 1;
                                   """;
                cmdA.Parameters.AddWithValue("$rid", requestId);
                cmdA.Parameters.AddWithValue("$now", TerminalReplicaDb.NowMs());
                cmdA.ExecuteNonQuery();
            }
            catch { }

            // If this was a Sale.Commit, capture saleId from payload if present.
            try
            {
                string? type = null;
                string? payload = null;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT type, payload_json FROM local_outbox WHERE request_id=$rid LIMIT 1;";
                    cmd.Parameters.AddWithValue("$rid", requestId);
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                    {
                        type = r.GetString(0);
                        payload = r.GetString(1);
                    }
                }

                if (string.Equals(type, "Sale.Commit", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(payload))
                {
                    var doc = JsonSerializer.Deserialize<JsonElement>(payload);
                    var term = doc.TryGetProperty("terminalId", out var tEl) ? tEl.GetGuid().ToString("D") : "";
                    var cash = doc.TryGetProperty("cashSessionId", out var cEl) ? cEl.GetGuid().ToString("D") : "";
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                                      INSERT OR IGNORE INTO committed_sales(request_id, sale_id, terminal_id, cash_session_id, created_at_ms)
                                      VALUES ($rid, $saleId, $term, $cash, $now);
                                      """;
                    cmd.Parameters.AddWithValue("$rid", requestId);
                    // We don't have saleId at this layer reliably; keep a placeholder so burn-in can still detect "committed command".
                    cmd.Parameters.AddWithValue("$saleId", requestId);
                    cmd.Parameters.AddWithValue("$term", term);
                    cmd.Parameters.AddWithValue("$cash", cash);
                    cmd.Parameters.AddWithValue("$now", TerminalReplicaDb.NowMs());
                    cmd.ExecuteNonQuery();
                }
            }
            catch { }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM local_outbox WHERE request_id=$rid;";
                cmd.Parameters.AddWithValue("$rid", requestId);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                                  UPDATE pending_sales
                                     SET status='committed',
                                         last_server_code='OK',
                                         last_error=NULL
                                   WHERE request_id=$rid;
                                  """;
                cmd.Parameters.AddWithValue("$rid", requestId);
                cmd.ExecuteNonQuery();
            }

            AppendReplayLog(conn, tx, "ack", requestId, "Sale.Commit acknowledged by server");
            tx.Commit();
        }, op: "mark_committed");
    }

    public void MarkRejected(string requestId, string code, string message, bool retryable, int attemptCount)
    {
        _db.WithWriteRetry(conn =>
        {
            using var tx = conn.BeginTransaction();

            var next = retryable ? ComputeNextRetryAtMs(attemptCount) : (long?)null;
            var state = retryable ? "pending" : "dead";

            if (!retryable)
            {
                DeadLetter(conn, tx, requestId, code, message, attemptCount);
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                                  UPDATE local_outbox
                                     SET state=$state,
                                         inflight_at_ms=NULL,
                                         attempt_count=$attempt,
                                         last_error=$err,
                                         next_retry_at_ms=$next
                                   WHERE request_id=$rid;
                                  """;
                cmd.Parameters.AddWithValue("$rid", requestId);
                cmd.Parameters.AddWithValue("$attempt", attemptCount);
                cmd.Parameters.AddWithValue("$err", $"{code}:{message}");
                cmd.Parameters.AddWithValue("$next", (object?)next ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$state", state);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                                  UPDATE pending_sales
                                     SET status=$st,
                                         last_server_code=$code,
                                         last_error=$msg
                                   WHERE request_id=$rid;
                                  """;
                cmd.Parameters.AddWithValue("$rid", requestId);
                cmd.Parameters.AddWithValue("$st", retryable ? "pending" : "dead");
                cmd.Parameters.AddWithValue("$code", code);
                cmd.Parameters.AddWithValue("$msg", message);
                cmd.ExecuteNonQuery();
            }

            AppendReplayLog(conn, tx, retryable ? "retry" : "dead", requestId, message, new { code, retryable, attemptCount });
            tx.Commit();
        }, op: "mark_rejected");
    }

    private void DeadLetter(SqliteConnection conn, SqliteTransaction tx, string requestId, string code, string message, int attemptCount)
    {
        // Snapshot payload for forensics/manual reprocessing.
        string type;
        string payload;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT type, payload_json FROM local_outbox WHERE request_id=$rid LIMIT 1;";
            cmd.Parameters.AddWithValue("$rid", requestId);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
                return;
            type = r.GetString(0);
            payload = r.GetString(1);
        }

        var (reason, classification) = ClassifyDeadReason(code);

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                              INSERT INTO dead_letter(request_id, type, dead_reason, classification, failed_at_ms, retry_count, last_server_code, last_error, payload_snapshot_json)
                              VALUES ($rid, $type, $reason, $class, $at, $retry, $code, $err, $payload)
                              ON CONFLICT(request_id) DO UPDATE SET
                                dead_reason=excluded.dead_reason,
                                classification=excluded.classification,
                                failed_at_ms=excluded.failed_at_ms,
                                retry_count=excluded.retry_count,
                                last_server_code=excluded.last_server_code,
                                last_error=excluded.last_error,
                                payload_snapshot_json=excluded.payload_snapshot_json;
                              """;
            cmd.Parameters.AddWithValue("$rid", requestId);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$reason", reason);
            cmd.Parameters.AddWithValue("$class", classification);
            cmd.Parameters.AddWithValue("$at", TerminalReplicaDb.NowMs());
            cmd.Parameters.AddWithValue("$retry", attemptCount);
            cmd.Parameters.AddWithValue("$code", code);
            cmd.Parameters.AddWithValue("$err", message);
            cmd.Parameters.AddWithValue("$payload", payload);
            cmd.ExecuteNonQuery();
        }
    }

    private static (string Reason, string Classification) ClassifyDeadReason(string code) => code switch
    {
        "LEASE_REVOKED" => ("LEASE_REVOKED", "consistency"),
        "LEASE_EXPIRED" => ("LEASE_EXPIRED", "consistency"),
        "LEASE_EXHAUSTED" => ("LEASE_EXHAUSTED", "consistency"),
        "INVALID_TERMINAL" => ("INVALID_TERMINAL", "security"),
        "VALIDATION_ERROR" => ("INVALID_PAYLOAD", "permanent"),
        _ => ("SERVER_REJECTED", "permanent")
    };

    public void DeadLetterByLease(string leaseId, string code, string message)
    {
        _db.WithWriteRetry(conn =>
        {
            using var tx = conn.BeginTransaction();

            // Dead-letter all pending sales tied to this lease.
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT request_id FROM pending_sales WHERE lease_id=$lease AND status='pending';";
                cmd.Parameters.AddWithValue("$lease", leaseId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var rid = r.GetString(0);
                    DeadLetter(conn, tx, rid, code, message, attemptCount: 0);
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                                  UPDATE pending_sales
                                     SET status='dead',
                                         last_server_code=$code,
                                         last_error=$msg
                                   WHERE lease_id=$lease AND status='pending';
                                  """;
                cmd.Parameters.AddWithValue("$lease", leaseId);
                cmd.Parameters.AddWithValue("$code", code);
                cmd.Parameters.AddWithValue("$msg", message);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                                  UPDATE local_outbox
                                     SET state='dead',
                                         inflight_at_ms=NULL,
                                         last_error=$err,
                                         next_retry_at_ms=NULL
                                   WHERE request_id IN (
                                       SELECT request_id FROM pending_sales WHERE lease_id=$lease
                                   );
                                  """;
                cmd.Parameters.AddWithValue("$lease", leaseId);
                cmd.Parameters.AddWithValue("$err", $"{code}:{message}");
                cmd.ExecuteNonQuery();
            }

            AppendReplayLog(conn, tx, "dead.lease", null, "Dead-lettered by lease invalidation", new { leaseId, code, message });
            tx.Commit();
        }, op: "deadletter_by_lease");
    }

    private static long ComputeNextRetryAtMs(int attemptCount)
    {
        // Exponential backoff with "full jitter" to avoid thundering herds.
        // Ceiling is intentionally low (2 min) because replay loops already pace and admission-gate.
        const int baseMs = 250;
        const int maxMs = 120_000;
        var a = Math.Clamp(attemptCount, 1, 30);
        var cap = (int)Math.Min(maxMs, baseMs * Math.Pow(2, Math.Min(16, a)));
        var delay = Random.Shared.Next(0, Math.Max(1, cap + 1));
        return DateTimeOffset.UtcNow.AddMilliseconds(delay).ToUnixTimeMilliseconds();
    }

    private static void AppendReplayLog(SqliteConnection conn, SqliteTransaction tx, string kind, string? requestId, string message, object? meta = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
                          INSERT INTO replay_log(at_ms, kind, request_id, message, meta_json)
                          VALUES ($at, $kind, $rid, $msg, $meta);
                          """;
        cmd.Parameters.AddWithValue("$at", TerminalReplicaDb.NowMs());
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$rid", (object?)requestId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$msg", message);
        cmd.Parameters.AddWithValue("$meta", meta == null ? (object)DBNull.Value : JsonSerializer.Serialize(meta));
        cmd.ExecuteNonQuery();
    }
}

public sealed record LocalOutboxItem(string RequestId, string Type, string PayloadJson, int AttemptCount);

