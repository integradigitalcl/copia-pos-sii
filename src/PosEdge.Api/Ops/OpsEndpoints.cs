using System.Text;
using Microsoft.EntityFrameworkCore;
using PosEdge.Api.Ops;
using PosEdge.Infrastructure;
using PosEdge.Shared;

namespace PosEdge.Api.Ops;

public static class OpsEndpoints
{
    public static void MapOps(WebApplication app)
    {
        app.MapGet("/terminal/status", (HttpRequest httpReq, IConfiguration cfg, Guid tenantId, Guid branchId, Guid terminalId, PosEdgeDbContext db) =>
        {
            // Server-side UX mapping uses latest terminal_status row when available.
            // If not present (no push), return a conservative offline message.
            var row = db.Database.SqlQuery<OpsTerminalStatusRow>($"""
                SELECT terminal_id AS "TerminalId",
                       COALESCE(health_state,'offline') AS "HealthState",
                       updated_at AS "UpdatedAt",
                       last_error AS "LastError"
                  FROM terminal_status
                 WHERE tenant_id = {tenantId} AND branch_id = {branchId} AND terminal_id = {terminalId}
                 LIMIT 1;
                """).FirstOrDefault();

            var hs = row?.HealthState ?? "offline";
            var dto = hs switch
            {
                "online" => new { status_code = "online", user_message = "Sincronizado", blocking = false, suggested_action = "Operación normal." },
                "offline" => new { status_code = "offline", user_message = "Trabajando sin conexión", blocking = false, suggested_action = "Continuar operando. Se sincronizará al reconectar." },
                "replaying" => new { status_code = "replaying", user_message = "Sincronizando ventas pendientes", blocking = false, suggested_action = "Esperar sincronización." },
                "waiting_replay" => new { status_code = "waiting_replay", user_message = "Esperando sincronización", blocking = false, suggested_action = "Esperar; el sistema regulará la sincronización." },
                "snapshot_backoff" => new { status_code = "snapshot_backoff", user_message = "Esperando recuperación", blocking = true, suggested_action = "Esperar recuperación automática." },
                "degraded" => new { status_code = "degraded", user_message = "Conectividad inestable", blocking = false, suggested_action = "Operar con precaución; revisar red/servidor." },
                "divergent" => new { status_code = "divergent", user_message = "Recuperando información", blocking = true, suggested_action = "Esperar recuperación automática." },
                "recovering" => new { status_code = "recovering", user_message = "Recuperando información", blocking = true, suggested_action = "Esperar recuperación automática." },
                "revoked" => new { status_code = "revoked", user_message = "Terminal requiere revisión", blocking = true, suggested_action = "Contactar administrador." },
                _ => new { status_code = hs, user_message = "Conectividad inestable", blocking = false, suggested_action = "Revisar estado de sincronización." }
            };
            return Results.Ok(new { ok = true, terminalId, updatedAt = row?.UpdatedAt, lastError = row?.LastError, status = dto });
        });

        // Terminal polls ops command queue (append-only). MVP: no terminal auth yet.
        app.MapGet("/terminal/ops/commands", async (Guid tenantId, Guid branchId, Guid terminalId, PosEdgeDbContext db, CancellationToken ct) =>
        {
            var rows = await db.Database.SqlQuery<TerminalOpsCommandRow>($"""
                SELECT command_id AS "CommandId",
                       command AS "Kind",
                       (meta->>'targetRequestId') AS "TargetRequestId",
                       NULLIF(meta->>'type','') AS "Type",
                       NULLIF(meta->>'payloadJson','') AS "PayloadJson"
                  FROM ops_terminal_commands c
                 WHERE c.tenant_id = {tenantId} AND c.branch_id = {branchId} AND c.terminal_id = {terminalId}
                   AND NOT EXISTS (
                     SELECT 1 FROM ops_terminal_command_acks a
                      WHERE a.tenant_id=c.tenant_id AND a.branch_id=c.branch_id AND a.terminal_id=c.terminal_id
                        AND a.command_id=c.command_id
                   )
                 ORDER BY c.at
                 LIMIT 50;
                """).ToListAsync(ct);
            return Results.Ok(new { ok = true, commands = rows });
        });

        app.MapPost("/terminal/ops/commands/ack", async (TerminalOpsCommandsAckHttp req, PosEdgeDbContext db, CancellationToken ct) =>
        {
            foreach (var cid in req.CommandIds)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO ops_terminal_command_acks(ack_id, tenant_id, branch_id, terminal_id, command_id, request_id, at, meta)
                    VALUES (gen_random_uuid(), {req.TenantId}, {req.BranchId}, {req.TerminalId}, {Guid.Parse(cid)}, {req.RequestId}, now(), '{{}}'::jsonb)
                    ON CONFLICT (tenant_id, branch_id, command_id) DO NOTHING;", ct);
            }
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/ops/replay/backlog", async (HttpRequest httpReq, IConfiguration cfg,
            Guid tenantId, Guid branchId, Guid? terminalId, string? state, string? type, long? olderThanMs,
            PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();

            var q = db.Database.SqlQuery<OpsReplayBacklogRow>($"""
                SELECT terminal_id AS "TerminalId",
                       request_id  AS "RequestId",
                       type        AS "Type",
                       state       AS "State",
                       attempt_count AS "AttemptCount",
                       inflight_at_ms AS "InflightAtMs",
                       created_at_ms  AS "CreatedAtMs",
                       next_retry_at_ms AS "NextRetryAtMs",
                       last_error   AS "LastError",
                       offline_mode AS "OfflineMode",
                       lease_id     AS "LeaseId"
                  FROM terminal_replay_items
                 WHERE tenant_id = {tenantId}
                   AND branch_id = {branchId}
                   AND ({terminalId} IS NULL OR terminal_id = {terminalId})
                   AND ({state} IS NULL OR state = {state})
                   AND ({type} IS NULL OR type = {type})
                   AND ({olderThanMs} IS NULL OR created_at_ms <= {olderThanMs})
                 ORDER BY created_at_ms
                 LIMIT 500;
                """);
            var rows = await q.ToListAsync(ct);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var enriched = rows.Select(r => new
            {
                r.TerminalId,
                r.RequestId,
                r.Type,
                status = r.State,
                retry_count = r.AttemptCount,
                inflight = r.InflightAtMs != null,
                created_at_ms = r.CreatedAtMs,
                last_attempt_at_ms = r.InflightAtMs,
                next_retry_at_ms = r.NextRetryAtMs,
                replay_age_ms = now - r.CreatedAtMs,
                lease_id = r.LeaseId,
                offline_mode = r.OfflineMode,
                last_error = r.LastError
            }).ToList();
            return Results.Ok(new { ok = true, items = enriched });
        });

        app.MapGet("/ops/replay/dead-letter", async (HttpRequest httpReq, IConfiguration cfg, Guid tenantId, Guid branchId, Guid? terminalId, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            var rows = await db.Database.SqlQuery<OpsReplayDeadLetterRow>($"""
                SELECT terminal_id AS "TerminalId",
                       request_id AS "RequestId",
                       type AS "Type",
                       dead_reason AS "DeadReason",
                       classification AS "Classification",
                       failed_at_ms AS "FailedAtMs",
                       retry_count AS "RetryCount",
                       last_server_code AS "LastServerCode",
                       last_error AS "LastError"
                  FROM terminal_dead_letters
                 WHERE tenant_id = {tenantId} AND branch_id = {branchId}
                   AND ({terminalId} IS NULL OR terminal_id = {terminalId})
                 ORDER BY failed_at_ms DESC
                 LIMIT 200;
                """).ToListAsync(ct);
            return Results.Ok(new { ok = true, deadLetters = rows });
        });

        // Detail: keep both {terminalId}/{requestId} and legacy {id} route for UX tools
        app.MapGet("/ops/replay/dead-letter/{terminalId:guid}/{requestId}", async (HttpRequest httpReq, IConfiguration cfg, Guid tenantId, Guid branchId, Guid terminalId, string requestId, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            var row = await db.Database.SqlQuery<OpsReplayDeadLetterDetailRow>($"""
                SELECT terminal_id AS "TerminalId",
                       request_id AS "RequestId",
                       type AS "Type",
                       dead_reason AS "DeadReason",
                       classification AS "Classification",
                       failed_at_ms AS "FailedAtMs",
                       retry_count AS "RetryCount",
                       last_server_code AS "LastServerCode",
                       last_error AS "LastError",
                       payload_snapshot_json AS "PayloadSnapshotJson"
                  FROM terminal_dead_letters
                 WHERE tenant_id = {tenantId} AND branch_id = {branchId}
                   AND terminal_id = {terminalId} AND request_id = {requestId}
                 LIMIT 1;
                """).FirstOrDefaultAsync(ct);
            return row == null ? Results.NotFound(new { ok = false, code = "NOT_FOUND" }) : Results.Ok(new { ok = true, deadLetter = row });
        });

        app.MapGet("/ops/replay/dead-letter/{id}", async (HttpRequest httpReq, IConfiguration cfg, Guid tenantId, Guid branchId, string id, PosEdgeDbContext db, CancellationToken ct) =>
        {
            // Expect format "terminalId:requestId"
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            var parts = id.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !Guid.TryParse(parts[0], out var terminalId))
                return Results.BadRequest(new { ok = false, code = "INVALID_ID", message = "Expected id=terminalId:requestId" });
            var requestId = parts[1];
            var row = await db.Database.SqlQuery<OpsReplayDeadLetterDetailRow>($"""
                SELECT terminal_id AS "TerminalId",
                       request_id AS "RequestId",
                       type AS "Type",
                       dead_reason AS "DeadReason",
                       classification AS "Classification",
                       failed_at_ms AS "FailedAtMs",
                       retry_count AS "RetryCount",
                       last_server_code AS "LastServerCode",
                       last_error AS "LastError",
                       payload_snapshot_json AS "PayloadSnapshotJson"
                  FROM terminal_dead_letters
                 WHERE tenant_id = {tenantId} AND branch_id = {branchId}
                   AND terminal_id = {terminalId} AND request_id = {requestId}
                 LIMIT 1;
                """).FirstOrDefaultAsync(ct);
            return row == null ? Results.NotFound(new { ok = false, code = "NOT_FOUND" }) : Results.Ok(new { ok = true, deadLetter = row });
        });

        app.MapPost("/ops/replay/retry", async (HttpRequest httpReq, IConfiguration cfg, OpsReplayActionHttp req, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            if (!req.Confirm) return Results.BadRequest(new { ok = false, code = "CONFIRM_REQUIRED" });
            if (string.IsNullOrWhiteSpace(req.TargetRequestId)) return Results.BadRequest(new { ok = false, code = "INVALID_TARGET" });
            var rid = string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!;
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ops_replay_actions(action_id, tenant_id, branch_id, terminal_id, request_id, at, action, target_request_id, actor_id, reason, meta)
                VALUES (gen_random_uuid(), {req.TenantId}, {req.BranchId}, {req.TerminalId}, {rid}, now(), 'retry', {req.TargetRequestId}, {req.ActorId}, {req.Reason}, {(req.MetaJson ?? "{}")}::jsonb)
                ON CONFLICT (tenant_id, branch_id, request_id) DO NOTHING;", ct);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO audit_log(audit_id, at, actor_type, actor_id, tenant_id, branch_id, request_id, action, entity_type, entity_id, classification, payload)
                VALUES (gen_random_uuid(), now(), 'user', {req.ActorId}, {req.TenantId}, {req.BranchId}, {rid},
                        'ops.replay.retry', 'TerminalReplayItem', NULL, 'ops',
                        jsonb_build_object('terminalId', {req.TerminalId}, 'targetRequestId', {req.TargetRequestId}, 'reason', {req.Reason}));", ct);

            // Enqueue terminal-side action (append-only)
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ops_terminal_commands(command_id, tenant_id, branch_id, terminal_id, request_id, at, command, actor_id, reason, meta)
                VALUES (gen_random_uuid(), {req.TenantId}, {req.BranchId}, {req.TerminalId}, {rid}, now(),
                        'replay.retry', {req.ActorId}, {req.Reason},
                        jsonb_build_object('targetRequestId', {req.TargetRequestId}));", ct);
            return Results.Ok(new { ok = true, requestId = rid });
        });

        app.MapPost("/ops/replay/purge", async (HttpRequest httpReq, IConfiguration cfg, OpsReplayActionHttp req, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            if (!req.Confirm) return Results.BadRequest(new { ok = false, code = "CONFIRM_REQUIRED" });
            if (string.IsNullOrWhiteSpace(req.TargetRequestId)) return Results.BadRequest(new { ok = false, code = "INVALID_TARGET" });
            var rid = string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!;
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ops_replay_actions(action_id, tenant_id, branch_id, terminal_id, request_id, at, action, target_request_id, actor_id, reason, meta)
                VALUES (gen_random_uuid(), {req.TenantId}, {req.BranchId}, {req.TerminalId}, {rid}, now(), 'purge', {req.TargetRequestId}, {req.ActorId}, {req.Reason}, {(req.MetaJson ?? "{}")}::jsonb)
                ON CONFLICT (tenant_id, branch_id, request_id) DO NOTHING;", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO audit_log(audit_id, at, actor_type, actor_id, tenant_id, branch_id, request_id, action, entity_type, entity_id, classification, payload)
                VALUES (gen_random_uuid(), now(), 'user', {req.ActorId}, {req.TenantId}, {req.BranchId}, {rid},
                        'ops.replay.purge', 'TerminalReplayItem', NULL, 'ops',
                        jsonb_build_object('terminalId', {req.TerminalId}, 'targetRequestId', {req.TargetRequestId}, 'reason', {req.Reason}));", ct);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ops_terminal_commands(command_id, tenant_id, branch_id, terminal_id, request_id, at, command, actor_id, reason, meta)
                VALUES (gen_random_uuid(), {req.TenantId}, {req.BranchId}, {req.TerminalId}, {rid}, now(),
                        'replay.purge', {req.ActorId}, {req.Reason},
                        jsonb_build_object('targetRequestId', {req.TargetRequestId}));", ct);
            return Results.Ok(new { ok = true, requestId = rid });
        });

        app.MapPost("/ops/replay/requeue", async (HttpRequest httpReq, IConfiguration cfg, OpsReplayActionHttp req, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            if (!req.Confirm) return Results.BadRequest(new { ok = false, code = "CONFIRM_REQUIRED" });
            if (string.IsNullOrWhiteSpace(req.TargetRequestId)) return Results.BadRequest(new { ok = false, code = "INVALID_TARGET" });
            if (string.IsNullOrWhiteSpace(req.Type) || string.IsNullOrWhiteSpace(req.PayloadJson))
                return Results.BadRequest(new { ok = false, code = "REQUEUE_REQUIRES_PAYLOAD" });
            var rid = string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!;
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ops_replay_actions(action_id, tenant_id, branch_id, terminal_id, request_id, at, action, target_request_id, actor_id, reason, meta)
                VALUES (gen_random_uuid(), {req.TenantId}, {req.BranchId}, {req.TerminalId}, {rid}, now(), 'requeue', {req.TargetRequestId}, {req.ActorId}, {req.Reason}, {(req.MetaJson ?? "{}")}::jsonb)
                ON CONFLICT (tenant_id, branch_id, request_id) DO NOTHING;", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO audit_log(audit_id, at, actor_type, actor_id, tenant_id, branch_id, request_id, action, entity_type, entity_id, classification, payload)
                VALUES (gen_random_uuid(), now(), 'user', {req.ActorId}, {req.TenantId}, {req.BranchId}, {rid},
                        'ops.replay.requeue', 'TerminalReplayItem', NULL, 'ops',
                        jsonb_build_object('terminalId', {req.TerminalId}, 'targetRequestId', {req.TargetRequestId}, 'reason', {req.Reason}));", ct);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ops_terminal_commands(command_id, tenant_id, branch_id, terminal_id, request_id, at, command, actor_id, reason, meta)
                VALUES (gen_random_uuid(), {req.TenantId}, {req.BranchId}, {req.TerminalId}, {rid}, now(),
                        'replay.requeue', {req.ActorId}, {req.Reason},
                        jsonb_build_object('targetRequestId', {req.TargetRequestId}, 'type', {req.Type}, 'payloadJson', {req.PayloadJson}));", ct);
            return Results.Ok(new { ok = true, requestId = rid });
        });

        app.MapGet("/ops/replay/health", async (HttpRequest httpReq, IConfiguration cfg, Guid tenantId, Guid branchId, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // Avoid EF SqlQuery scalar wrapper quirks (trailing semicolons etc.). Use raw ExecuteScalar.
            async Task<T?> ScalarAsync<T>(string sql, Action<System.Data.Common.DbCommand> bind, CancellationToken ct2)
            {
                await using var cmd = db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = sql;
                bind(cmd);
                if (cmd.Connection!.State != System.Data.ConnectionState.Open)
                    await cmd.Connection.OpenAsync(ct2);
                var o = await cmd.ExecuteScalarAsync(ct2);
                if (o == null || o is DBNull) return default;
                var t = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                return (T)Convert.ChangeType(o, t);
            }

            var depth = await ScalarAsync<int?>("SELECT COUNT(*) FROM terminal_replay_items WHERE tenant_id=@t AND branch_id=@b AND state='pending'", cmd =>
            {
                var p1 = cmd.CreateParameter(); p1.ParameterName = "@t"; p1.Value = tenantId; cmd.Parameters.Add(p1);
                var p2 = cmd.CreateParameter(); p2.ParameterName = "@b"; p2.Value = branchId; cmd.Parameters.Add(p2);
            }, ct) ?? 0;

            var dead = await ScalarAsync<int?>("SELECT COUNT(*) FROM terminal_dead_letters WHERE tenant_id=@t AND branch_id=@b", cmd =>
            {
                var p1 = cmd.CreateParameter(); p1.ParameterName = "@t"; p1.Value = tenantId; cmd.Parameters.Add(p1);
                var p2 = cmd.CreateParameter(); p2.ParameterName = "@b"; p2.Value = branchId; cmd.Parameters.Add(p2);
            }, ct) ?? 0;

            var oldest = await ScalarAsync<long?>("SELECT MIN(created_at_ms) FROM terminal_replay_items WHERE tenant_id=@t AND branch_id=@b AND state='pending'", cmd =>
            {
                var p1 = cmd.CreateParameter(); p1.ParameterName = "@t"; p1.Value = tenantId; cmd.Parameters.Add(p1);
                var p2 = cmd.CreateParameter(); p2.ParameterName = "@b"; p2.Value = branchId; cmd.Parameters.Add(p2);
            }, ct);

            var stuck = await ScalarAsync<int?>("SELECT COUNT(*) FROM terminal_replay_items WHERE tenant_id=@t AND branch_id=@b AND state='inflight' AND inflight_at_ms IS NOT NULL AND inflight_at_ms <= @cutoff", cmd =>
            {
                var p1 = cmd.CreateParameter(); p1.ParameterName = "@t"; p1.Value = tenantId; cmd.Parameters.Add(p1);
                var p2 = cmd.CreateParameter(); p2.ParameterName = "@b"; p2.Value = branchId; cmd.Parameters.Add(p2);
                var p3 = cmd.CreateParameter(); p3.ParameterName = "@cutoff"; p3.Value = now - 60000; cmd.Parameters.Add(p3);
            }, ct) ?? 0;
            return Results.Ok(new
            {
                ok = true,
                backlog_depth = depth,
                dead_letter_count = dead,
                oldest_replay_age_ms = oldest.HasValue ? (now - oldest.Value) : 0,
                stuck_inflight = stuck
            });
        });

        app.MapGet("/ops/replay/timeline/{terminalId:guid}/{requestId}", async (HttpRequest httpReq, IConfiguration cfg, Guid tenantId, Guid branchId, Guid terminalId, string requestId, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            var rows = await db.Database.SqlQuery<OpsReplayTimelineRow>($"""
                SELECT at_ms AS "AtMs",
                       kind AS "Kind",
                       request_id AS "RequestId",
                       message AS "Message",
                       meta_json AS "MetaJson"
                  FROM terminal_replay_log
                 WHERE tenant_id = {tenantId} AND branch_id = {branchId}
                   AND terminal_id = {terminalId}
                   AND (request_id = {requestId} OR request_id IS NULL)
                 ORDER BY at_ms;
                """).ToListAsync(ct);
            return Results.Ok(new { ok = true, terminalId, requestId, timeline = rows });
        });

        app.MapPost("/ops/reconciliation/scan-now", async (HttpRequest httpReq, IConfiguration cfg, PosEdgeDbContext db, IServiceProvider sp, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            // Trigger scan by inserting an audit event; worker scans on schedule. For immediate scan we run the scan inline.
            var w = sp.GetServices<IHostedService>().OfType<PosEdge.Workers.ReconciliationScanWorker>().FirstOrDefault();
            if (w == null) return Results.Problem("ReconciliationScanWorker not registered", statusCode: 500);
            await w.ScanOnce(ct);
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/ops/reconciliation/ack", async (HttpRequest httpReq, IConfiguration cfg, OpsReconActionHttp req, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            var rid = string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!;
            var actorId = req.ActorId;
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO reconciliation_actions(action_id, tenant_id, branch_id, finding_id, at, action, actor_type, actor_id, request_id, note, meta)
                VALUES (gen_random_uuid(), {req.TenantId}, {req.BranchId}, {req.FindingId}, now(), 'ack', 'ops_user', {actorId}, {rid}, {req.Note}, {(req.MetaJson ?? "{}")}::jsonb)
                ON CONFLICT (tenant_id, branch_id, request_id) DO NOTHING;", ct);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO audit_log(audit_id, at, actor_type, actor_id, tenant_id, branch_id, request_id, action, entity_type, entity_id, classification, payload)
                VALUES (gen_random_uuid(), now(), 'user', {actorId}, {req.TenantId}, {req.BranchId}, {rid},
                        'ops.reconciliation.ack', 'ReconciliationFinding', {req.FindingId}, 'ops',
                        jsonb_build_object('note', {req.Note}));", ct);
            return Results.Ok(new { ok = true, requestId = rid });
        });

        app.MapPost("/ops/reconciliation/resolve", async (HttpRequest httpReq, IConfiguration cfg, OpsReconActionHttp req, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            var rid = string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!;
            var actorId = req.ActorId;
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO reconciliation_actions(action_id, tenant_id, branch_id, finding_id, at, action, actor_type, actor_id, request_id, note, meta)
                VALUES (gen_random_uuid(), {req.TenantId}, {req.BranchId}, {req.FindingId}, now(), 'resolve', 'ops_user', {actorId}, {rid}, {req.Note}, {(req.MetaJson ?? "{}")}::jsonb)
                ON CONFLICT (tenant_id, branch_id, request_id) DO NOTHING;", ct);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO audit_log(audit_id, at, actor_type, actor_id, tenant_id, branch_id, request_id, action, entity_type, entity_id, classification, payload)
                VALUES (gen_random_uuid(), now(), 'user', {actorId}, {req.TenantId}, {req.BranchId}, {rid},
                        'ops.reconciliation.resolve', 'ReconciliationFinding', {req.FindingId}, 'ops',
                        jsonb_build_object('note', {req.Note}));", ct);
            return Results.Ok(new { ok = true, requestId = rid });
        });

        app.MapGet("/ops/cash/sessions/open", async (HttpRequest httpReq, IConfiguration cfg, Guid tenantId, Guid branchId, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            var rows = await db.CashSessions.AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.BranchId == branchId && s.Status == "open")
                .OrderByDescending(s => s.OpenedAt)
                .Select(s => new { s.CashSessionId, s.TerminalId, s.OpenedAt, s.OpenedBy, s.OpeningAmount, s.ReconciliationStatus })
                .ToListAsync(ct);
            return Results.Ok(new { ok = true, sessions = rows });
        });

        app.MapGet("/ops/cash/discrepancies/open", async (HttpRequest httpReq, IConfiguration cfg, Guid tenantId, Guid branchId, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            var rows = await db.Database.SqlQuery<OpsCashDiscrepancyRow>($"""
                SELECT discrepancy_id AS "DiscrepancyId",
                       cash_session_id AS "CashSessionId",
                       detected_at AS "DetectedAt",
                       expected_amount AS "ExpectedAmount",
                       counted_amount AS "CountedAmount",
                       discrepancy_amount AS "DiscrepancyAmount",
                       severity AS "Severity",
                       note AS "Note"
                  FROM cash_discrepancies
                 WHERE tenant_id = {tenantId} AND branch_id = {branchId}
                 ORDER BY detected_at DESC
                 LIMIT 200;
                """).ToListAsync(ct);
            return Results.Ok(new { ok = true, discrepancies = rows });
        });

        app.MapGet("/ops/cash/discrepancies/{id:guid}", async (HttpRequest httpReq, IConfiguration cfg, Guid id, Guid tenantId, Guid branchId, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            var row = await db.Database.SqlQuery<OpsCashDiscrepancyRow>($"""
                SELECT discrepancy_id AS "DiscrepancyId",
                       cash_session_id AS "CashSessionId",
                       detected_at AS "DetectedAt",
                       expected_amount AS "ExpectedAmount",
                       counted_amount AS "CountedAmount",
                       discrepancy_amount AS "DiscrepancyAmount",
                       severity AS "Severity",
                       note AS "Note"
                  FROM cash_discrepancies
                 WHERE tenant_id = {tenantId} AND branch_id = {branchId}
                   AND discrepancy_id = {id}
                 LIMIT 1;
                """).FirstOrDefaultAsync(ct);
            return row == null ? Results.NotFound(new { ok = false, code = "NOT_FOUND" }) : Results.Ok(new { ok = true, discrepancy = row });
        });

        app.MapGet("/ops/journals/imbalanced", async (HttpRequest httpReq, IConfiguration cfg, Guid tenantId, Guid branchId, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            var rows = await db.FinancialJournal.AsNoTracking()
                .Where(j => j.TenantId == tenantId && j.BranchId == branchId && j.TotalDebit != j.TotalCredit)
                .OrderByDescending(j => j.At)
                .Select(j => new { j.JournalId, j.RequestId, j.Kind, j.At, j.TotalDebit, j.TotalCredit })
                .Take(200)
                .ToListAsync(ct);
            return Results.Ok(new { ok = true, journals = rows });
        });

        app.MapGet("/ops/reports/cash-reconciliation.csv", async (HttpRequest httpReq, IConfiguration cfg, Guid tenantId, Guid branchId, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            var sessions = await db.CashSessions.AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.BranchId == branchId)
                .OrderByDescending(s => s.OpenedAt)
                .Take(200)
                .Select(s => new { s.CashSessionId, s.TerminalId, s.Status, s.OpenedAt, s.ClosedAt, s.OpeningAmount, s.ExpectedAmount, s.CountedAmount, s.DiscrepancyAmount, s.ReconciliationStatus })
                .ToListAsync(ct);

            var sb = new StringBuilder();
            sb.AppendLine("cash_session_id,terminal_id,status,opened_at,closed_at,opening_amount,expected_amount,counted_amount,discrepancy_amount,reconciliation_status");
            foreach (var s in sessions)
            {
                sb.Append(s.CashSessionId).Append(',')
                    .Append(s.TerminalId).Append(',')
                    .Append(s.Status).Append(',')
                    .Append(s.OpenedAt.ToString("O")).Append(',')
                    .Append(s.ClosedAt?.ToString("O") ?? "").Append(',')
                    .Append(s.OpeningAmount).Append(',')
                    .Append(s.ExpectedAmount?.ToString() ?? "").Append(',')
                    .Append(s.CountedAmount?.ToString() ?? "").Append(',')
                    .Append(s.DiscrepancyAmount?.ToString() ?? "").Append(',')
                    .Append(s.ReconciliationStatus ?? "").AppendLine();
            }
            return Results.Text(sb.ToString(), "text/csv");
        });

        // ===== Terminal fleet panel =====
        app.MapGet("/ops/terminals", async (HttpRequest httpReq, IConfiguration cfg,
            Guid tenantId, Guid? branchId, string? health_state, bool? offline_only, bool? divergent_only,
            PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            var staleCut = DateTimeOffset.UtcNow.AddSeconds(-15);

            var rows = await db.Database.SqlQuery<OpsTerminalFleetRow>($"""
                SELECT t.terminal_id AS "TerminalId",
                       t.branch_id   AS "BranchId",
                       COALESCE(ts.last_seen_at, t.last_seen_at) AS "LastSeenAt",
                       COALESCE(ts.health_state, 'offline') AS "HealthState",
                       COALESCE(ts.replay_backlog, 0) AS "ReplayBacklog",
                       COALESCE(ts.dead_letter_count, 0) AS "DeadLetterCount",
                       ts.last_applied_seq AS "LastAppliedSeq",
                       ts.active_lease_id AS "ActiveLeaseId",
                       ts.last_error AS "LastError",
                       ts.meta AS "Meta"
                  FROM terminals t
                  LEFT JOIN terminal_status ts
                    ON ts.tenant_id = t.tenant_id AND ts.branch_id = t.branch_id AND ts.terminal_id = t.terminal_id
                 WHERE t.tenant_id = {tenantId}
                   AND t.deleted_at IS NULL
                   AND ({branchId} IS NULL OR t.branch_id = {branchId})
                   AND ({health_state} IS NULL OR COALESCE(ts.health_state, 'offline') = {health_state})
                 ORDER BY COALESCE(ts.last_seen_at, t.last_seen_at) DESC NULLS LAST
                 LIMIT 500;
                """).ToListAsync(ct);

            var terminals = rows.Where(r =>
            {
                if (offline_only == true)
                {
                    if (r.LastSeenAt != null && r.LastSeenAt >= staleCut) return false;
                }
                if (divergent_only == true)
                {
                    if (!string.Equals(r.HealthState, "divergent", StringComparison.OrdinalIgnoreCase)) return false;
                }
                return true;
            }).Select(r =>
            {
                var online = r.LastSeenAt != null && r.LastSeenAt >= staleCut;
                var ux = r.HealthState switch
                {
                    "online" => "Sincronizado",
                    "offline" => "Trabajando sin conexión",
                    "replaying" => "Sincronizando ventas pendientes",
                    "waiting_replay" => "Esperando sincronización",
                    "snapshot_backoff" => "Esperando recuperación",
                    "degraded" => "Conectividad inestable",
                    "recovering_degraded" => "Conectividad inestable",
                    "divergent" => "Recuperando información",
                    "recovering" => "Recuperando información",
                    "revoked" => "Terminal requiere revisión",
                    "paused" => "Sincronización pausada",
                    "reconnecting" => "Reconectando",
                    _ => "Conectividad inestable"
                };

                string? degradedReason = null;
                string? version = null;
                string? build = null;
                DateTimeOffset? leaseExpiration = null;
                int? localQueueDepth = null;
                DateTimeOffset? lastReplayAt = null;
                DateTimeOffset? lastSnapshotAt = null;

                if (r.Meta is { ValueKind: System.Text.Json.JsonValueKind.Object } meta)
                {
                    if (meta.TryGetProperty("degradedReason", out var dr) && dr.ValueKind == System.Text.Json.JsonValueKind.String)
                        degradedReason = dr.GetString();
                    if (meta.TryGetProperty("version", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                        version = v.GetString();
                    if (meta.TryGetProperty("build", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.String)
                        build = b.GetString();
                    if (meta.TryGetProperty("leaseExpiresAtMs", out var le) && le.ValueKind == System.Text.Json.JsonValueKind.Number)
                        leaseExpiration = DateTimeOffset.FromUnixTimeMilliseconds(le.GetInt64());
                    if (meta.TryGetProperty("localQueueDepth", out var lqd) && lqd.ValueKind == System.Text.Json.JsonValueKind.Number)
                        localQueueDepth = lqd.GetInt32();
                    if (meta.TryGetProperty("lastReplayAtMs", out var lra) && lra.ValueKind == System.Text.Json.JsonValueKind.Number)
                        lastReplayAt = DateTimeOffset.FromUnixTimeMilliseconds(lra.GetInt64());
                    if (meta.TryGetProperty("lastSnapshotAtMs", out var lsa) && lsa.ValueKind == System.Text.Json.JsonValueKind.Number)
                        lastSnapshotAt = DateTimeOffset.FromUnixTimeMilliseconds(lsa.GetInt64());
                }

                return new
                {
                    r.TerminalId,
                    r.BranchId,
                    online,
                    ux_status = ux,
                    health_state = r.HealthState,
                    replay_backlog = r.ReplayBacklog,
                    dead_letter_count = r.DeadLetterCount,
                    replay_lag = (long?)null,
                    snapshot_state = (string?)null,
                    divergence_state = (string?)null,
                    active_lease = r.ActiveLeaseId,
                    lease_expiration = leaseExpiration,
                    local_queue_depth = localQueueDepth,
                    last_seq = r.LastAppliedSeq,
                    last_seen_at = r.LastSeenAt,
                    last_replay_at = lastReplayAt,
                    last_snapshot_at = lastSnapshotAt,
                    version,
                    build,
                    degraded_reason = degradedReason,
                    last_error = r.LastError
                };
            }).ToList();

            return Results.Ok(new { ok = true, terminals });
        });

        app.MapGet("/ops/terminals/{terminalId:guid}", async (HttpRequest httpReq, IConfiguration cfg,
            Guid tenantId, Guid branchId, Guid terminalId, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();

            var status = await db.Database.SqlQuery<OpsTerminalStatusDetailRow>($"""
                SELECT t.terminal_id AS "TerminalId",
                       t.branch_id   AS "BranchId",
                       t.name        AS "Name",
                       t.machine_name AS "MachineName",
                       t.status      AS "Status",
                       COALESCE(ts.last_seen_at, t.last_seen_at) AS "LastSeenAt",
                       COALESCE(ts.health_state, 'offline') AS "HealthState",
                       COALESCE(ts.replay_backlog, 0) AS "ReplayBacklog",
                       COALESCE(ts.dead_letter_count, 0) AS "DeadLetterCount",
                       ts.last_applied_seq AS "LastAppliedSeq",
                       ts.active_lease_id AS "ActiveLeaseId",
                       ts.last_error AS "LastError",
                       ts.meta AS "Meta"
                  FROM terminals t
                  LEFT JOIN terminal_status ts
                    ON ts.tenant_id = t.tenant_id AND ts.branch_id = t.branch_id AND ts.terminal_id = t.terminal_id
                 WHERE t.tenant_id = {tenantId} AND t.branch_id = {branchId} AND t.terminal_id = {terminalId}
                 LIMIT 1;
                """).FirstOrDefaultAsync(ct);
            if (status == null) return Results.NotFound(new { ok = false, code = "NOT_FOUND" });

            var timeline = await db.Database.SqlQuery<OpsReplayTimelineRow>($"""
                SELECT at_ms AS "AtMs",
                       kind AS "Kind",
                       request_id AS "RequestId",
                       message AS "Message",
                       meta_json AS "MetaJson"
                  FROM terminal_replay_log
                 WHERE tenant_id = {tenantId} AND branch_id = {branchId} AND terminal_id = {terminalId}
                 ORDER BY at_ms DESC
                 LIMIT 200;
                """).ToListAsync(ct);

            var cmds = await db.Database.SqlQuery<OpsTerminalCommandRow>($"""
                SELECT c.command_id AS "CommandId",
                       c.at AS "At",
                       c.command AS "Command",
                       c.request_id AS "RequestId",
                       c.actor_id AS "ActorId",
                       c.reason AS "Reason",
                       EXISTS(
                         SELECT 1 FROM ops_terminal_command_acks a
                          WHERE a.tenant_id=c.tenant_id AND a.branch_id=c.branch_id AND a.terminal_id=c.terminal_id AND a.command_id=c.command_id
                       ) AS "Acked"
                  FROM ops_terminal_commands c
                 WHERE c.tenant_id = {tenantId} AND c.branch_id = {branchId} AND c.terminal_id = {terminalId}
                 ORDER BY c.at DESC
                 LIMIT 50;
                """).ToListAsync(ct);

            var dl = await db.Database.SqlQuery<OpsReplayDeadLetterRow>($"""
                SELECT terminal_id AS "TerminalId",
                       request_id AS "RequestId",
                       type AS "Type",
                       dead_reason AS "DeadReason",
                       classification AS "Classification",
                       failed_at_ms AS "FailedAtMs",
                       retry_count AS "RetryCount",
                       last_server_code AS "LastServerCode",
                       last_error AS "LastError"
                  FROM terminal_dead_letters
                 WHERE tenant_id = {tenantId} AND branch_id = {branchId}
                   AND terminal_id = {terminalId}
                 ORDER BY failed_at_ms DESC
                 LIMIT 50;
                """).ToListAsync(ct);

            return Results.Ok(new { ok = true, terminal = status, replay_timeline = timeline, ops_commands = cmds, dead_letters = dl });
        });

        app.MapPost("/ops/terminal/force-resync", async (HttpRequest httpReq, IConfiguration cfg, OpsTerminalActionHttp req, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            if (!req.Confirm) return Results.BadRequest(new { ok = false, code = "CONFIRM_REQUIRED" });
            var rid = string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!;
            await InsertTerminalCommand(db, req, rid, "force_resync", ct);
            await InsertAudit(db, req, rid, "ops.terminal.force_resync", ct);
            return Results.Ok(new { ok = true, requestId = rid });
        });

        app.MapPost("/ops/terminal/force-snapshot", async (HttpRequest httpReq, IConfiguration cfg, OpsTerminalActionHttp req, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            if (!req.Confirm) return Results.BadRequest(new { ok = false, code = "CONFIRM_REQUIRED" });
            var rid = string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!;
            await InsertTerminalCommand(db, req, rid, "force_snapshot", ct);
            await InsertAudit(db, req, rid, "ops.terminal.force_snapshot", ct);
            return Results.Ok(new { ok = true, requestId = rid });
        });

        app.MapPost("/ops/terminal/clear-divergence", async (HttpRequest httpReq, IConfiguration cfg, OpsTerminalActionHttp req, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            if (!req.Confirm) return Results.BadRequest(new { ok = false, code = "CONFIRM_REQUIRED" });
            var rid = string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!;
            await InsertTerminalCommand(db, req, rid, "clear_divergence", ct);
            await InsertAudit(db, req, rid, "ops.terminal.clear_divergence", ct);
            return Results.Ok(new { ok = true, requestId = rid });
        });

        app.MapPost("/ops/terminal/revoke", async (HttpRequest httpReq, IConfiguration cfg, OpsTerminalActionHttp req, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            if (!req.Confirm) return Results.BadRequest(new { ok = false, code = "CONFIRM_REQUIRED" });
            var rid = string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!;
            await InsertTerminalCommand(db, req, rid, "revoke", ct);
            await InsertAudit(db, req, rid, "ops.terminal.revoke", ct);
            return Results.Ok(new { ok = true, requestId = rid });
        });

        app.MapPost("/ops/terminal/reconnect", async (HttpRequest httpReq, IConfiguration cfg, OpsTerminalActionHttp req, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            if (!req.Confirm) return Results.BadRequest(new { ok = false, code = "CONFIRM_REQUIRED" });
            var rid = string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!;
            await InsertTerminalCommand(db, req, rid, "reconnect", ct);
            await InsertAudit(db, req, rid, "ops.terminal.reconnect", ct);
            return Results.Ok(new { ok = true, requestId = rid });
        });

        app.MapPost("/ops/terminal/pause-replay", async (HttpRequest httpReq, IConfiguration cfg, OpsTerminalActionHttp req, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            if (!req.Confirm) return Results.BadRequest(new { ok = false, code = "CONFIRM_REQUIRED" });
            var rid = string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!;
            await InsertTerminalCommand(db, req, rid, "pause_replay", ct);
            await InsertAudit(db, req, rid, "ops.terminal.pause_replay", ct);
            return Results.Ok(new { ok = true, requestId = rid });
        });

        app.MapPost("/ops/terminal/resume-replay", async (HttpRequest httpReq, IConfiguration cfg, OpsTerminalActionHttp req, PosEdgeDbContext db, CancellationToken ct) =>
        {
            if (!OpsAuth.IsAuthorized(httpReq, cfg)) return Results.Unauthorized();
            if (!req.Confirm) return Results.BadRequest(new { ok = false, code = "CONFIRM_REQUIRED" });
            var rid = string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!;
            await InsertTerminalCommand(db, req, rid, "resume_replay", ct);
            await InsertAudit(db, req, rid, "ops.terminal.resume_replay", ct);
            return Results.Ok(new { ok = true, requestId = rid });
        });
    }

    private static Task InsertTerminalCommand(PosEdgeDbContext db, OpsTerminalActionHttp req, string rid, string command, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ops_terminal_commands(command_id, tenant_id, branch_id, terminal_id, request_id, at, command, actor_id, reason, meta)
            VALUES (gen_random_uuid(), {req.TenantId}, {req.BranchId}, {req.TerminalId}, {rid}, now(), {command}, {req.ActorId}, {req.Reason}, {(req.MetaJson ?? "{}")}::jsonb)
            ON CONFLICT (tenant_id, branch_id, request_id) DO NOTHING;", ct);

    private static Task InsertAudit(PosEdgeDbContext db, OpsTerminalActionHttp req, string rid, string action, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO audit_log(audit_id, at, actor_type, actor_id, tenant_id, branch_id, request_id, action, entity_type, entity_id, classification, payload)
            VALUES (gen_random_uuid(), now(), 'user', {req.ActorId}, {req.TenantId}, {req.BranchId}, {rid},
                    {action}, 'Terminal', {req.TerminalId}, 'ops',
                    jsonb_build_object('reason', {req.Reason}, 'meta', {(req.MetaJson ?? "{}")}::jsonb));", ct);
}

public sealed record TerminalOpsCommandRow(Guid CommandId, string Kind, string? TargetRequestId, string? Type, string? PayloadJson);
public sealed record TerminalOpsCommandsAckHttp(Guid TenantId, Guid BranchId, Guid TerminalId, List<string> CommandIds, string RequestId);

public sealed record OpsReconActionHttp(Guid TenantId, Guid BranchId, Guid FindingId, Guid? ActorId, string? RequestId, string? Note, string? MetaJson);

public sealed record OpsCashDiscrepancyRow(
    Guid DiscrepancyId,
    Guid CashSessionId,
    DateTimeOffset DetectedAt,
    decimal ExpectedAmount,
    decimal CountedAmount,
    decimal DiscrepancyAmount,
    string Severity,
    string? Note);

public sealed record OpsTerminalStatusRow(Guid TerminalId, string HealthState, DateTimeOffset UpdatedAt, string? LastError);

public sealed record OpsTerminalFleetRow(Guid TerminalId, Guid BranchId, DateTimeOffset? LastSeenAt, string HealthState, int ReplayBacklog, int DeadLetterCount, long? LastAppliedSeq, Guid? ActiveLeaseId, string? LastError, System.Text.Json.JsonElement? Meta);
public sealed record OpsTerminalStatusDetailRow(Guid TerminalId, Guid BranchId, string Name, string MachineName, string Status, DateTimeOffset? LastSeenAt, string HealthState, int ReplayBacklog, int DeadLetterCount, long? LastAppliedSeq, Guid? ActiveLeaseId, string? LastError, System.Text.Json.JsonElement? Meta);
public sealed record OpsTerminalCommandRow(Guid CommandId, DateTimeOffset At, string Command, string RequestId, Guid? ActorId, string? Reason, bool Acked);
public sealed record OpsTerminalActionHttp(Guid TenantId, Guid BranchId, Guid TerminalId, Guid? ActorId, string? RequestId, string? Reason, bool Confirm, string? MetaJson);

public sealed record OpsReplayBacklogRow(Guid TerminalId, string RequestId, string Type, string State, int AttemptCount, long? InflightAtMs, long CreatedAtMs, long? NextRetryAtMs, string? LastError, string? OfflineMode, Guid? LeaseId);
public sealed record OpsReplayDeadLetterRow(Guid TerminalId, string RequestId, string Type, string DeadReason, string Classification, long FailedAtMs, int RetryCount, string? LastServerCode, string? LastError);
public sealed record OpsReplayDeadLetterDetailRow(Guid TerminalId, string RequestId, string Type, string DeadReason, string Classification, long FailedAtMs, int RetryCount, string? LastServerCode, string? LastError, string PayloadSnapshotJson);
public sealed record OpsReplayActionHttp(
    Guid TenantId,
    Guid BranchId,
    Guid TerminalId,
    string TargetRequestId,
    Guid? ActorId,
    string? RequestId,
    string? Reason,
    bool Confirm,
    string? Type,
    string? PayloadJson,
    string? MetaJson);
public sealed record OpsReplayTimelineRow(long AtMs, string Kind, string? RequestId, string Message, string? MetaJson);

