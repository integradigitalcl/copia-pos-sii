using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using PosEdge.Api.Bootstrap;
using PosEdge.Application.Leases;
using PosEdge.Application.Sales;
using PosEdge.Application.Sync;
using PosEdge.Application.Admission;
using PosEdge.Application.Cash;
using PosEdge.Contracts.Protocol;
using PosEdge.Api.Ops;
using PosEdge.Infrastructure;
using PosEdge.Shared;
using PosEdge.Workers;
using Makaretu.Dns;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<PosEdgeDbContext>(o =>
{
    var cs = builder.Configuration.GetConnectionString("PosEdge");
    // MVP: keep execution strategy simple (no retry strategy) to allow explicit transactions.
    // We'll re-introduce retries later via DbContext.Database.CreateExecutionStrategy().
    o.UseNpgsql(cs);
});

builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

// OpenTelemetry (traces only for now) + Prometheus metrics endpoint (prometheus-net)
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("PosEdge.Api"))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation();
        t.AddHttpClientInstrumentation();
        t.AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m.AddRuntimeInstrumentation();
        m.AddAspNetCoreInstrumentation();
        // Exported via prometheus-net middleware; OTel metrics pipeline still useful for in-proc instruments.
    });

builder.Services.AddMediatR(typeof(SaleCommitHandler).Assembly);

builder.Services.AddSignalR()
    .AddMessagePackProtocol();

builder.Services.AddHostedService<OutboxDispatcherWorker>();
builder.Services.AddHostedService<LeaseExpirationWorker>();
builder.Services.AddHostedService<ReconciliationScanWorker>();
builder.Services.AddHostedService<OpsMetricsWorker>();
builder.Services.AddHostedService<DiscoveryBeaconWorker>();
builder.Services.AddHostedService<MdnsAdvertiserWorker>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Prometheus metrics scraping
app.UseHttpMetrics();
app.MapMetrics("/metrics");

app.MapGet("/health/live", () => Results.Ok(new { ok = true, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));

app.MapGet("/health/ready", async (PosEdgeDbContext db, CancellationToken ct) =>
{
    try
    {
        await db.Database.ExecuteSqlRawAsync("SELECT 1;", ct);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 503);
    }
});

app.MapGet("/health/sync", async (Guid tenantId, Guid branchId, PosEdgeDbContext db, CancellationToken ct) =>
{
    var lastSeq = await db.EventLog.AsNoTracking()
        .Where(e => e.TenantId == tenantId && e.BranchId == branchId)
        .Select(e => (long?)e.Seq)
        .MaxAsync(ct) ?? 0L;
    return Results.Ok(new { ok = true, lastSeq });
});

// Cluster identity (anti split-brain authority)
app.MapGet("/v1/cluster/identity", () =>
{
    var id = ClusterIdentityManager.GetOrCreateIdentity();
    return Results.Ok(new
    {
        ok = true,
        clusterId = id.ClusterId,
        primaryServerId = id.PrimaryServerId,
        fingerprint = id.Fingerprint,
        publicKeyPem = id.PublicKeyPem,
        signedAuthorityToken = id.SignedAuthorityToken,
        issuedAtMs = id.IssuedAtMs
    });
});

// Ops endpoints (admin key protected)
app.MapGet("/ops/reconciliation/findings", async (HttpRequest httpReq, IConfiguration cfg, Guid tenantId, Guid branchId, PosEdgeDbContext db, CancellationToken ct) =>
{
    if (!OpsAuth.IsAuthorized(httpReq, cfg))
        return Results.Unauthorized();

    // MVP: return last 200 findings as JSON (meta stays jsonb).
    var rows = await db.Database.SqlQuery<OpsFindingRow>($"""
        SELECT finding_id       AS "FindingId",
               finding_type     AS "FindingType",
               severity         AS "Severity",
               detected_at      AS "DetectedAt",
               status           AS "Status",
               entity_type      AS "EntityType",
               entity_id        AS "EntityId",
               cash_session_id  AS "CashSessionId",
               request_id       AS "RequestId",
               message          AS "Message"
          FROM reconciliation_findings
         WHERE tenant_id = {tenantId} AND branch_id = {branchId}
         ORDER BY detected_at DESC
         LIMIT 200;
        """).ToListAsync(ct);

    return Results.Ok(new { ok = true, findings = rows });
});

OpsEndpoints.MapOps(app);

// Admission control (global replay/snapshot throttling)
app.MapPost("/v1/admission/claim", async (AdmissionClaimHttp req, IMediator mediator, CancellationToken ct) =>
{
    var cmd = new AdmissionClaimCommand(req.TenantId, req.BranchId, req.TerminalId, req.Kind,
        req.TtlSeconds <= 0 ? 10 : req.TtlSeconds,
        string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!);
    var r = await mediator.Send(cmd, ct);
    return Results.Ok(r);
});

// Terminal → server replay mirror (for ops visibility)
app.MapPost("/v1/terminal/replay-mirror", async (TerminalReplayMirrorHttp req, PosEdgeDbContext db, CancellationToken ct) =>
{
    // MVP: trust local network; production should authenticate terminal.
    // IMPORTANT: This is a *snapshot* of the terminal's local outbox/dead-letter at a point in time.
    // We must purge mirror rows that disappeared locally, otherwise ops/burn-in will see phantom inflight forever.
    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    foreach (var it in req.Items)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO terminal_replay_items(tenant_id, branch_id, terminal_id, request_id, type, state, created_at_ms, inflight_at_ms, attempt_count, last_error, next_retry_at_ms, offline_mode, lease_id, last_updated_at)
            VALUES ({req.TenantId}, {req.BranchId}, {req.TerminalId}, {it.RequestId}, {it.Type}, {it.State}, {it.CreatedAtMs}, {it.InflightAtMs}, {it.AttemptCount}, {it.LastError}, {it.NextRetryAtMs}, NULL, NULL, now())
            ON CONFLICT (tenant_id, branch_id, terminal_id, request_id)
            DO UPDATE SET state=excluded.state,
                          inflight_at_ms=excluded.inflight_at_ms,
                          attempt_count=excluded.attempt_count,
                          last_error=excluded.last_error,
                          next_retry_at_ms=excluded.next_retry_at_ms,
                          last_updated_at=now();", ct);
    }

    // Mirror cleanup: anything not present in current snapshot is considered committed/purged and should disappear.
    // We use a time-based heuristic so a transient publish failure doesn't wipe state: only delete rows that weren't updated by this publish.
    // First mark current rows with a unique "last_updated_at" by updating above, then delete stale rows older than ~30s.
    await db.Database.ExecuteSqlInterpolatedAsync($@"
        DELETE FROM terminal_replay_items
         WHERE tenant_id = {req.TenantId} AND branch_id = {req.BranchId} AND terminal_id = {req.TerminalId}
           AND last_updated_at < (now() - interval '30 seconds');", ct);

    foreach (var d in req.DeadLetters)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO terminal_dead_letters(tenant_id, branch_id, terminal_id, request_id, type, dead_reason, classification, failed_at_ms, retry_count, last_server_code, last_error, payload_snapshot_json)
            VALUES ({req.TenantId}, {req.BranchId}, {req.TerminalId}, {d.RequestId}, {d.Type}, {d.DeadReason}, {d.Classification}, {d.FailedAtMs}, {d.RetryCount}, {d.LastServerCode}, {d.LastError}, {d.PayloadSnapshotJson})
            ON CONFLICT (tenant_id, branch_id, terminal_id, request_id)
            DO UPDATE SET dead_reason=excluded.dead_reason,
                          classification=excluded.classification,
                          failed_at_ms=excluded.failed_at_ms,
                          retry_count=excluded.retry_count,
                          last_server_code=excluded.last_server_code,
                          last_error=excluded.last_error,
                          payload_snapshot_json=excluded.payload_snapshot_json;", ct);
    }

    // Dead-letter cleanup: if a request is no longer dead locally, remove it from mirror to prevent phantom dead letters.
    await db.Database.ExecuteSqlInterpolatedAsync($@"
        DELETE FROM terminal_dead_letters
         WHERE tenant_id = {req.TenantId} AND branch_id = {req.BranchId} AND terminal_id = {req.TerminalId}
           AND failed_at_ms < ({nowMs} - 30_000)
           AND NOT EXISTS (
             SELECT 1
               FROM terminal_replay_log l
              WHERE l.tenant_id = {req.TenantId} AND l.branch_id = {req.BranchId} AND l.terminal_id = {req.TerminalId}
                AND l.request_id = terminal_dead_letters.request_id
                AND l.kind IN ('dead','dead.lease')
           );", ct);

    // Replay log mirror (append-only)
    if (req.ReplayLog != null && req.ReplayLog.Count > 0)
    {
        foreach (var l in req.ReplayLog)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO terminal_replay_log(tenant_id, branch_id, terminal_id, at_ms, kind, request_id, message, meta_json)
                VALUES ({req.TenantId}, {req.BranchId}, {req.TerminalId}, {l.AtMs}, {l.Kind}, {l.RequestId}, {l.Message}, {l.MetaJson});", ct);
        }
    }

    // Upsert terminal_status (mutable derived view) for fleet panel.
    var now = DateTimeOffset.UtcNow;
    var lastAppliedSeq = req.TerminalHealth?.LastAppliedSeq;
    var healthState = req.TerminalHealth?.State ?? "offline";
    var backlog = req.Items?.Count(i => (i.State == "pending" || i.State == "retry_wait" || i.State == "inflight")) ?? 0;
    var deadCount = req.DeadLetters?.Count ?? 0;
    var activeLease = req.TerminalHealth?.ActiveLeaseId;
    var lastErr = req.TerminalHealth?.LastError;
    var meta = new
    {
        version = req.TerminalHealth?.Version,
        build = req.TerminalHealth?.Build,
        degradedReason = req.TerminalHealth?.DegradedReason,
        lastReplayAtMs = req.TerminalHealth?.LastReplayAtMs,
        lastSnapshotAtMs = req.TerminalHealth?.LastSnapshotAtMs,
        localQueueDepth = req.TerminalHealth?.LocalQueueDepth,
        lastSyncOkAtMs = req.TerminalHealth?.LastSyncOkAtMs
    };

    await db.Database.ExecuteSqlInterpolatedAsync($@"
        INSERT INTO terminal_status(tenant_id, branch_id, terminal_id, updated_at, last_seen_at, health_state, last_applied_seq, last_server_seq, replay_backlog, dead_letter_count, active_lease_id, divergence_state, snapshot_state, last_error, meta)
        VALUES ({req.TenantId}, {req.BranchId}, {req.TerminalId}, now(), {now}, {healthState}, {lastAppliedSeq}, NULL, {backlog}, {deadCount}, {activeLease}, NULL, NULL, {lastErr}, {System.Text.Json.JsonSerializer.Serialize(meta)}::jsonb)
        ON CONFLICT (tenant_id, branch_id, terminal_id)
        DO UPDATE SET updated_at=now(),
                      last_seen_at=excluded.last_seen_at,
                      health_state=excluded.health_state,
                      last_applied_seq=excluded.last_applied_seq,
                      replay_backlog=excluded.replay_backlog,
                      dead_letter_count=excluded.dead_letter_count,
                      active_lease_id=excluded.active_lease_id,
                      last_error=excluded.last_error,
                      meta=excluded.meta;", ct);

    return Results.Ok(new { ok = true });
});

app.MapPost("/v1/admission/release", async (AdmissionReleaseHttp req, IMediator mediator, CancellationToken ct) =>
{
    var cmd = new AdmissionReleaseCommand(req.Kind, req.Token);
    var r = await mediator.Send(cmd, ct);
    return Results.Ok(r);
});

// Lease acquisition / renewal (MVP)
app.MapPost("/v1/leases/request", async (LeaseRequestHttp req, IMediator mediator, CancellationToken ct) =>
{
    var cmd = new LeaseRequestCommand(req.TenantId, req.BranchId, req.TerminalId,
        string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!,
        req.TtlSeconds,
        req.Lines.Select(l => new LeaseRequestLine(l.ProductId, l.Qty)).ToList());
    var r = await mediator.Send(cmd, ct);
    return Results.Ok(r);
});

app.MapPost("/v1/leases/renew", async (LeaseRenewHttp req, IMediator mediator, CancellationToken ct) =>
{
    var cmd = new LeaseRenewCommand(req.TenantId, req.BranchId, req.TerminalId, req.LeaseId,
        string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!,
        req.ExtendSeconds);
    var r = await mediator.Send(cmd, ct);
    return Results.Ok(r);
});

app.MapPost("/v1/leases/revoke", async (LeaseRevokeHttp req, IMediator mediator, CancellationToken ct) =>
{
    var cmd = new LeaseRevokeCommand(req.TenantId, req.BranchId, req.LeaseId, req.TerminalId,
        string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!,
        string.IsNullOrWhiteSpace(req.Reason) ? "admin" : req.Reason,
        req.Note);
    var r = await mediator.Send(cmd, ct);
    return Results.Ok(r);
});

// HTTP endpoint for Sale.Commit (MVP)
app.MapPost("/v1/sales/commit", async (SaleCommitHttpRequest req, IMediator mediator, CancellationToken ct) =>
{
    var cmd = req.ToCommand();
    var result = await mediator.Send(cmd, ct);
    return Results.Ok(result);
});

// Sale void (financial closure MVP)
app.MapPost("/v1/sales/void", async (SaleVoidHttpRequest req, IMediator mediator, CancellationToken ct) =>
{
    var cmd = new SaleVoidCommand(req.TenantId, req.BranchId, req.TerminalId, req.CashSessionId, req.SaleId,
        string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!,
        string.IsNullOrWhiteSpace(req.Reason) ? "void" : req.Reason);
    var r = await mediator.Send(cmd, ct);
    return Results.Ok(r);
});

// Sale refund / credit note (financial closure)
app.MapPost("/v1/sales/refund", async (SaleRefundHttpRequest req, IMediator mediator, CancellationToken ct) =>
{
    var cmd = new SaleRefundCommand(
        req.TenantId,
        req.BranchId,
        req.TerminalId,
        req.CashSessionId,
        req.SaleId,
        string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!,
        string.IsNullOrWhiteSpace(req.Kind) ? "refund" : req.Kind,
        req.Lines.Select(l => new RefundLineSpec(l.SaleLineId, l.Qty)).ToList(),
        req.Payments?.Select(p => new RefundPaymentSpec(p.Method, p.Amount, p.Currency, p.Meta)).ToList(),
        req.Note);
    var r = await mediator.Send(cmd, ct);
    return Results.Ok(r);
});

// Cash session open/close
app.MapPost("/v1/cash/sessions/open", async (CashSessionOpenHttp req, IMediator mediator, CancellationToken ct) =>
{
    var cmd = new CashSessionOpenCommand(req.TenantId, req.BranchId, req.TerminalId, req.OpenedBy, req.OpeningAmount,
        string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!);
    var r = await mediator.Send(cmd, ct);
    return Results.Ok(r);
});

app.MapPost("/v1/cash/sessions/close", async (CashSessionCloseHttp req, IMediator mediator, CancellationToken ct) =>
{
    var cmd = new CashSessionCloseCommand(req.TenantId, req.BranchId, req.TerminalId, req.CashSessionId, req.ClosedBy,
        string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!,
        req.Note);
    var r = await mediator.Send(cmd, ct);
    return Results.Ok(r);
});

app.MapPost("/v1/cash/counts/record", async (CashCountRecordHttp req, IMediator mediator, CancellationToken ct) =>
{
    var cmd = new CashCountRecordCommand(req.TenantId, req.BranchId, req.TerminalId, req.CashSessionId, req.CountedBy,
        string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!,
        string.IsNullOrWhiteSpace(req.Currency) ? "MXN" : req.Currency,
        req.Lines.Select(l => new CashCountLineSpec(l.Denomination, l.Quantity)).ToList(),
        req.Note);
    var r = await mediator.Send(cmd, ct);
    return Results.Ok(r);
});

app.MapPost("/v1/cash/discrepancies/resolve", async (CashDiscrepancyResolveHttp req, IMediator mediator, CancellationToken ct) =>
{
    var cmd = new CashDiscrepancyResolveCommand(req.TenantId, req.BranchId, req.TerminalId, req.CashSessionId, req.DiscrepancyId,
        req.ResolvedBy,
        string.IsNullOrWhiteSpace(req.RequestId) ? Ulid.NewUlidString() : req.RequestId!,
        string.IsNullOrWhiteSpace(req.Resolution) ? "accepted" : req.Resolution,
        req.Note);
    var r = await mediator.Send(cmd, ct);
    return Results.Ok(r);
});

// Replay batching endpoint (storm hardening): commit multiple Sale.Commit in one roundtrip.
app.MapPost("/v1/replay/sales/commit-batch", async (List<SaleCommitHttpRequest> reqs, IMediator mediator, CancellationToken ct) =>
{
    if (reqs.Count == 0) return Results.Ok(new { ok = true, results = Array.Empty<object>() });
    if (reqs.Count > 50) reqs = reqs.Take(50).ToList();

    var results = new List<object>(reqs.Count);
    foreach (var r in reqs)
    {
        var cmd = r.ToCommand();
        var res = await mediator.Send(cmd, ct);
        results.Add(new { requestId = cmd.RequestId, ok = res.Ok, code = res.Code, message = res.Message, saleId = res.SaleId, ticket = res.ServerTicket, eventSeq = res.EventSeq });
    }
    return Results.Ok(new { ok = true, results });
});

// Sync pull endpoint (MVP)
app.MapGet("/v1/sync", async (Guid tenantId, Guid branchId, long fromSeq, int limit, PosEdgeDbContext db, CancellationToken ct) =>
{
    limit = Math.Clamp(limit, 1, 5000);
    var events = await db.EventLog.AsNoTracking()
        .Where(e => e.TenantId == tenantId && e.BranchId == branchId && e.Seq > fromSeq)
        .OrderBy(e => e.Seq)
        .Take(limit)
        .Select(e => new { e.Seq, e.At, e.Type, e.EntityType, e.EntityId, e.TerminalId, e.RequestId, e.DataJson })
        .ToListAsync(ct);
    var nextFrom = events.Count == 0 ? fromSeq : events[^1].Seq;
    return Results.Ok(new { nextFromSeq = nextFrom, hasMore = events.Count == limit, events });
});

// Full snapshot endpoint (for divergence recovery)
app.MapGet("/v1/snapshot", async (Guid tenantId, Guid branchId, bool hashOnly, bool includeLeases, IMediator mediator, CancellationToken ct) =>
{
    var q = new SnapshotGetQuery(tenantId, branchId, hashOnly, includeLeases);
    var r = await mediator.Send(q, ct);
    return Results.Ok(r);
});

app.MapHub<MulticajaHub>("/hubs/multicaja");

// Apply schema + seed only when explicitly enabled via configuration.
// Production deployments must apply schema out-of-band (psql / migration job) to avoid unintended mutation on restart.
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("bootstrap");
    await SchemaBootstrapper.ApplySchemaAndSeedAsync(app.Configuration, logger, app.Lifetime.ApplicationStopping);
}

app.Run();

public sealed record SaleCommitHttpRequest(
    Guid TenantId,
    Guid BranchId,
    Guid TerminalId,
    Guid CashSessionId,
    string? RequestId,
    string Currency,
    decimal DiscountTotal,
    Guid? LeaseId,
    List<SaleCommitHttpLine> Lines,
    List<SaleCommitHttpPayment> Payments)
{
    public SaleCommitCommand ToCommand() => new(
        TenantId,
        BranchId,
        TerminalId,
        CashSessionId,
        string.IsNullOrWhiteSpace(RequestId) ? Ulid.NewUlidString() : RequestId!,
        Currency,
        Lines.Select(l => new SaleCommitLine(l.ProductId, l.Sku, l.Name, l.Qty, l.UnitPrice, l.TaxRate)).ToList(),
        Payments.Select(p => new SaleCommitPayment(p.Method, p.Amount, p.Currency, p.Meta)).ToList(),
        DiscountTotal,
        LeaseId);
}

public sealed record SaleCommitHttpLine(Guid ProductId, string Sku, string Name, decimal Qty, decimal UnitPrice, decimal TaxRate);
public sealed record SaleCommitHttpPayment(string Method, decimal Amount, string Currency, object? Meta);

public sealed class DiscoveryBeaconWorker : BackgroundService
{
    // Reuse the same port already used by Grunflex POS discovery to simplify firewall/rules.
    public const int DiscoveryPort = 33279;
    private const string Magic = "POSEDGE-DISCOVER";

    private readonly ILogger<DiscoveryBeaconWorker> _log;
    private readonly IConfiguration _cfg;

    public DiscoveryBeaconWorker(ILogger<DiscoveryBeaconWorker> log, IConfiguration cfg)
    {
        _log = log;
        _cfg = cfg;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        System.Net.Sockets.UdpClient? udp = null;
        try
        {
            udp = new System.Net.Sockets.UdpClient(System.Net.Sockets.AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, DiscoveryPort));
            _log.LogInformation("Discovery beacon listening on UDP {Port}", DiscoveryPort);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Discovery beacon failed to bind UDP {Port}. Discovery disabled.", DiscoveryPort);
            udp?.Dispose();
            return;
        }

        var apiPort = ResolveApiPort();
        var version = typeof(DiscoveryBeaconWorker).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var identity = ClusterIdentityManager.GetOrCreateIdentity();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(stoppingToken).ConfigureAwait(false);
                var payload = System.Text.Encoding.UTF8.GetString(result.Buffer);
                if (string.IsNullOrEmpty(payload) || !payload.StartsWith(Magic, StringComparison.Ordinal))
                    continue;

                var requestId = payload.Length > Magic.Length + 1
                    ? payload[(Magic.Length + 1)..].Trim()
                    : string.Empty;

                var ip = BestLocalIp(result.RemoteEndPoint.Address);
                var response = new
                {
                    server = Environment.MachineName,
                    ip,
                    apiPort,
                    apiBaseUrl = $"http://{ip}:{apiPort}/",
                    version,
                    clusterId = identity.ClusterId,
                    fingerprint = identity.Fingerprint,
                    requestId
                };
                var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response);
                await udp.SendAsync(bytes, bytes.Length, result.RemoteEndPoint).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Discovery beacon error; continuing.");
                try { await Task.Delay(250, stoppingToken).ConfigureAwait(false); } catch { }
            }
        }

        udp.Dispose();
    }

    private int ResolveApiPort()
    {
        try
        {
            var urls = _cfg["urls"] ?? _cfg["ASPNETCORE_URLS"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
            if (!string.IsNullOrWhiteSpace(urls))
            {
                var first = urls.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(first) && Uri.TryCreate(first, UriKind.Absolute, out var uri))
                    return uri.Port;
            }
        }
        catch { }
        return 5071;
    }

    internal static string BestLocalIp(System.Net.IPAddress remote)
    {
        try
        {
            var candidates = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(u => new { Addr = u.Address, Mask = u.IPv4Mask })
                .ToList();

            if (remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var rem = remote.GetAddressBytes();
                foreach (var c in candidates)
                {
                    if (c.Mask == null) continue;
                    var ip = c.Addr.GetAddressBytes();
                    var mask = c.Mask.GetAddressBytes();
                    if (mask.Length != 4 || ip.Length != 4) continue;
                    var sameSubnet = true;
                    for (var i = 0; i < 4; i++)
                    {
                        if ((ip[i] & mask[i]) != (rem[i] & mask[i])) { sameSubnet = false; break; }
                    }
                    if (sameSubnet) return c.Addr.ToString();
                }
            }

            return candidates.FirstOrDefault()?.Addr.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}

public sealed class MdnsAdvertiserWorker : BackgroundService
{
    private readonly ILogger<MdnsAdvertiserWorker> _log;
    private readonly IConfiguration _cfg;

    public MdnsAdvertiserWorker(ILogger<MdnsAdvertiserWorker> log, IConfiguration cfg)
    {
        _log = log;
        _cfg = cfg;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var identity = ClusterIdentityManager.GetOrCreateIdentity();
            var apiPort = ResolveApiPort();

            // Service name must match DiscoveryCli query.
            var profile = new ServiceProfile(identity.ClusterId, "_posedgetcp._tcp", (ushort)apiPort);
            var ip = DiscoveryBeaconWorker.BestLocalIp(System.Net.IPAddress.Parse("127.0.0.1"));
            profile.AddProperty("url", $"http://{ip}:{apiPort}/");
            profile.AddProperty("fingerprint", identity.Fingerprint);
            profile.AddProperty("clusterId", identity.ClusterId);

            using var sd = new ServiceDiscovery();
            sd.Advertise(profile);
            sd.Mdns.Start();

            _log.LogInformation("mDNS advertiser started: {Service} port={Port}", "_posedgetcp._tcp.local", apiPort);

            while (!stoppingToken.IsCancellationRequested)
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "mDNS advertiser failed; continuing without mDNS.");
        }
    }

    private int ResolveApiPort()
    {
        try
        {
            var urls = _cfg["urls"] ?? _cfg["ASPNETCORE_URLS"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
            if (!string.IsNullOrWhiteSpace(urls))
            {
                var first = urls.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(first) && Uri.TryCreate(first, UriKind.Absolute, out var uri))
                    return uri.Port;
            }
        }
        catch { }
        return 5071;
    }
}
public sealed record SaleVoidHttpRequest(Guid TenantId, Guid BranchId, Guid TerminalId, Guid CashSessionId, Guid SaleId, string? RequestId, string Reason);
public sealed record SaleRefundHttpRequest(
    Guid TenantId,
    Guid BranchId,
    Guid TerminalId,
    Guid CashSessionId,
    Guid SaleId,
    string? RequestId,
    string Kind,
    List<SaleRefundHttpLine> Lines,
    List<SaleRefundHttpPayment>? Payments,
    string? Note);

public sealed record SaleRefundHttpLine(Guid SaleLineId, decimal Qty);
public sealed record SaleRefundHttpPayment(string Method, decimal Amount, string Currency, object? Meta);

public sealed record CashSessionOpenHttp(Guid TenantId, Guid BranchId, Guid TerminalId, Guid OpenedBy, decimal OpeningAmount, string? RequestId);
public sealed record CashSessionCloseHttp(Guid TenantId, Guid BranchId, Guid TerminalId, Guid CashSessionId, Guid ClosedBy, string? RequestId, string? Note);
public sealed record CashCountRecordHttp(Guid TenantId, Guid BranchId, Guid TerminalId, Guid CashSessionId, Guid CountedBy, string? RequestId,
    string Currency, List<CashCountRecordHttpLine> Lines, string? Note);
public sealed record CashCountRecordHttpLine(decimal Denomination, int Quantity);
public sealed record CashDiscrepancyResolveHttp(Guid TenantId, Guid BranchId, Guid TerminalId, Guid CashSessionId, Guid DiscrepancyId, Guid ResolvedBy,
    string? RequestId, string Resolution, string? Note);

public sealed record LeaseRequestHttp(Guid TenantId, Guid BranchId, Guid TerminalId, string? RequestId, int TtlSeconds,
    List<LeaseRequestHttpLine> Lines);
public sealed record LeaseRequestHttpLine(Guid ProductId, decimal Qty);
public sealed record LeaseRenewHttp(Guid TenantId, Guid BranchId, Guid TerminalId, Guid LeaseId, string? RequestId, int ExtendSeconds);
public sealed record LeaseRevokeHttp(Guid TenantId, Guid BranchId, Guid LeaseId, Guid? TerminalId, string? RequestId, string Reason, string? Note);
public sealed record AdmissionClaimHttp(Guid TenantId, Guid BranchId, Guid TerminalId, string Kind, int TtlSeconds, string? RequestId);
public sealed record AdmissionReleaseHttp(string Kind, string Token);
public sealed record TerminalReplayMirrorHttp(
    Guid TenantId,
    Guid BranchId,
    Guid TerminalId,
    long AtMs,
    List<TerminalReplayMirrorItem> Items,
    List<TerminalReplayMirrorDeadLetter> DeadLetters,
    List<TerminalReplayMirrorLogRow>? ReplayLog,
    TerminalHealthMirror? TerminalHealth);
public sealed record TerminalReplayMirrorItem(string RequestId, string Type, string State, long CreatedAtMs, long? InflightAtMs, int AttemptCount, string? LastError, long? NextRetryAtMs);
public sealed record TerminalReplayMirrorDeadLetter(string RequestId, string Type, string DeadReason, string Classification, long FailedAtMs, int RetryCount, string? LastServerCode, string? LastError, string PayloadSnapshotJson);
public sealed record TerminalReplayMirrorLogRow(long AtMs, string Kind, string? RequestId, string Message, string? MetaJson);
public sealed record TerminalHealthMirror(
    string State,
    long UpdatedAtMs,
    long? LastAppliedSeq,
    long? LastServerSeq,
    string? LastError,
    Guid? ActiveLeaseId,
    long? LeaseExpiresAtMs,
    int? LocalQueueDepth,
    long? LastReplayAtMs,
    long? LastSnapshotAtMs,
    long? LastSyncOkAtMs,
    string? Version,
    string? Build,
    string? DegradedReason);

public sealed record OpsFindingRow(
    Guid FindingId,
    string FindingType,
    string Severity,
    DateTimeOffset DetectedAt,
    string Status,
    string? EntityType,
    Guid? EntityId,
    Guid? CashSessionId,
    string? RequestId,
    string Message);

public sealed class MulticajaHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("Server.HelloAck", new { serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        await base.OnConnectedAsync();
    }

    public async Task ClientHello(McEnvelope env)
    {
        // MVP: group by tenant/branch so outbox broadcasts can target.
        var key = $"{env.TenantId:N}:{env.BranchId:N}";
        await Groups.AddToGroupAsync(Context.ConnectionId, key);
        await Clients.Caller.SendAsync("Server.AuthOk", new { ok = true, key, serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }

    public Task ClientHeartbeat(McEnvelope env) =>
        Clients.Caller.SendAsync("Server.HeartbeatAck", new { ok = true, serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
}

public sealed class OutboxDispatcherWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<OutboxDispatcherWorker> _log;

    public OutboxDispatcherWorker(IServiceProvider sp, ILogger<OutboxDispatcherWorker> log)
    {
        _sp = sp;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchOnce(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Outbox dispatch loop error");
            }

            try { await Task.Delay(300, stoppingToken); } catch { }
        }
    }

    private async Task DispatchOnce(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PosEdgeDbContext>();
        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<MulticajaHub>>();

        // Pull a few pending outbox items with SKIP LOCKED to allow multiple workers later.
        var items = await db.Outbox
            .FromSqlRaw(@"
                SELECT * FROM outbox
                 WHERE status = 'pending' AND (next_retry_at IS NULL OR next_retry_at <= now())
                 ORDER BY created_at
                 FOR UPDATE SKIP LOCKED
                 LIMIT 50")
            .ToListAsync(ct);

        if (items.Count == 0)
            return;

        foreach (var item in items)
        {
            try
            {
                var group = $"{item.TenantId:N}:{item.BranchId:N}";
                await hub.Clients.Group(group).SendAsync("Server.Event", item.PayloadJson, ct);
                item.Status = "delivered";
                item.Attempts += 1;
                item.LastError = null;
                item.NextRetryAt = null;
            }
            catch (Exception ex)
            {
                item.Attempts += 1;
                item.LastError = ex.Message;
                item.NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, 1 + item.Attempts * 2));
                item.Status = item.Attempts >= 20 ? "dead" : "pending";
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
