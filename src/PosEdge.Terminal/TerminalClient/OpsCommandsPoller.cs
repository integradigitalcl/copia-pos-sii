using Microsoft.Data.Sqlite;
using PosEdge.Terminal.Replica;
using System.Net.Http.Json;
using PosEdge.Shared;

namespace PosEdge.Terminal.Client;

internal sealed class OpsCommandsPoller
{
    private readonly HttpClient _http;
    private readonly TerminalReplicaDb _replica;
    private readonly Guid _tenantId;
    private readonly Guid _branchId;
    private readonly Guid _terminalId;

    public OpsCommandsPoller(HttpClient http, TerminalReplicaDb replica, Guid tenantId, Guid branchId, Guid terminalId)
    {
        _http = http;
        _replica = replica;
        _tenantId = tenantId;
        _branchId = branchId;
        _terminalId = terminalId;
    }

    public async Task PollAndApplyOnceAsync(CancellationToken ct)
    {
        // This endpoint already exists as an append-only command queue.
        var resp = await _http.GetFromJsonAsync<OpsCommandsHttp>($"/terminal/ops/commands?tenantId={_tenantId}&branchId={_branchId}&terminalId={_terminalId}", ct);
        if (resp?.Commands == null || resp.Commands.Count == 0) return;

        foreach (var cmd in resp.Commands)
            Apply(cmd);

        // Ack (idempotent) so server can mark delivered.
        await _http.PostAsJsonAsync("/terminal/ops/commands/ack",
            new OpsCommandsAckHttp(_tenantId, _branchId, _terminalId, resp.Commands.Select(c => c.CommandId.ToString("D")).ToList(), Ulid.NewUlidString()),
            ct);
    }

    private void Apply(OpsCommandRow cmd)
    {
        // Minimal, safe actions:
        // - replay.retry: move dead_letter → local_outbox pending
        // - replay.purge: mark purged in dead_letter + ensure not in local_outbox
        // - replay.requeue: insert to local_outbox if absent (payload must be provided in meta)
        _replica.WithWriteRetry(conn =>
        {
            using var tx = conn.BeginTransaction();

            if (cmd.Kind == "pause_replay")
            {
                using var st = conn.CreateCommand();
                st.Transaction = tx;
                st.CommandText = """
                                 UPDATE terminal_health
                                    SET state='paused',
                                        updated_at_ms=$now,
                                        last_error='paused_by_ops'
                                  WHERE tenant_id=$t AND branch_id=$b AND terminal_id=$term;
                                 """;
                st.Parameters.AddWithValue("$now", TerminalReplicaDb.NowMs());
                st.Parameters.AddWithValue("$t", _tenantId.ToString("D"));
                st.Parameters.AddWithValue("$b", _branchId.ToString("D"));
                st.Parameters.AddWithValue("$term", _terminalId.ToString("D"));
                st.ExecuteNonQuery();
            }
            else if (cmd.Kind == "resume_replay")
            {
                using var st = conn.CreateCommand();
                st.Transaction = tx;
                st.CommandText = """
                                 UPDATE terminal_health
                                    SET state='online',
                                        updated_at_ms=$now,
                                        last_error=NULL
                                  WHERE tenant_id=$t AND branch_id=$b AND terminal_id=$term;
                                 """;
                st.Parameters.AddWithValue("$now", TerminalReplicaDb.NowMs());
                st.Parameters.AddWithValue("$t", _tenantId.ToString("D"));
                st.Parameters.AddWithValue("$b", _branchId.ToString("D"));
                st.Parameters.AddWithValue("$term", _terminalId.ToString("D"));
                st.ExecuteNonQuery();
            }
            else if (cmd.Kind == "replay.retry")
            {
                using var s = conn.CreateCommand();
                s.Transaction = tx;
                s.CommandText = """
                                INSERT INTO local_outbox(request_id, type, state, payload_json, created_at_ms, inflight_at_ms, attempt_count, last_error, next_retry_at_ms)
                                SELECT request_id, type, 'pending', payload_snapshot_json, @now, NULL, 0, NULL, NULL
                                  FROM dead_letter
                                 WHERE request_id=@rid
                                ON CONFLICT(request_id) DO NOTHING;
                                """;
                s.Parameters.Add(new SqliteParameter("@rid", cmd.TargetRequestId));
                s.Parameters.Add(new SqliteParameter("@now", TerminalReplicaDb.NowMs()));
                s.ExecuteNonQuery();
            }
            else if (cmd.Kind == "replay.purge")
            {
                using var d = conn.CreateCommand();
                d.Transaction = tx;
                d.CommandText = "DELETE FROM local_outbox WHERE request_id=@rid;";
                d.Parameters.Add(new SqliteParameter("@rid", cmd.TargetRequestId));
                d.ExecuteNonQuery();

                using var u = conn.CreateCommand();
                u.Transaction = tx;
                u.CommandText = "UPDATE dead_letter SET classification='purged', dead_reason='purged_by_ops' WHERE request_id=@rid;";
                u.Parameters.Add(new SqliteParameter("@rid", cmd.TargetRequestId));
                u.ExecuteNonQuery();
            }
            else if (cmd.Kind == "replay.requeue")
            {
                var payloadJson = cmd.PayloadJson ?? "{}";
                var type = cmd.Type ?? "Unknown";
                using var i = conn.CreateCommand();
                i.Transaction = tx;
                i.CommandText = """
                                INSERT INTO local_outbox(request_id, type, state, payload_json, created_at_ms, inflight_at_ms, attempt_count, last_error, next_retry_at_ms)
                                VALUES (@rid, @t, 'pending', @p, @now, NULL, 0, NULL, NULL)
                                ON CONFLICT(request_id) DO NOTHING;
                                """;
                i.Parameters.Add(new SqliteParameter("@rid", cmd.TargetRequestId));
                i.Parameters.Add(new SqliteParameter("@t", type));
                i.Parameters.Add(new SqliteParameter("@p", payloadJson));
                i.Parameters.Add(new SqliteParameter("@now", TerminalReplicaDb.NowMs()));
                i.ExecuteNonQuery();
            }

            tx.Commit();
        }, "ops_cmd_apply");
    }
}

internal sealed record OpsCommandsHttp(List<OpsCommandRow> Commands);
internal sealed record OpsCommandRow(Guid CommandId, string Kind, string TargetRequestId, string? Type, string? PayloadJson);
internal sealed record OpsCommandsAckHttp(Guid TenantId, Guid BranchId, Guid TerminalId, List<string> CommandIds, string RequestId);

