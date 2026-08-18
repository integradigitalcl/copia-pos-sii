using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR.Client;
using PosEdge.Contracts.Protocol;
using PosEdge.Shared;
using PosEdge.Terminal.Offline;
using PosEdge.Terminal.Replica;

namespace PosEdge.Terminal.Client;

public sealed class TerminalClient
{
    private readonly Guid _tenantId;
    private readonly Guid _branchId;
    private readonly Guid _terminalId;
    private readonly Guid _cashSessionId;
    private readonly string _apiBase;
    private readonly TerminalReplicaDb _replica;
    private readonly EventApplyEngine _apply;
    private readonly LocalOutboxStore _outbox;
    private HubConnection? _hub;
    private readonly HttpClient _http = new();
    private ReplayMirrorPublisher? _mirror;
    private OpsCommandsPoller? _ops;

    public OfflineMode OfflineMode { get; set; } = OfflineMode.Strict;
    public Guid? ActiveLeaseId { get; set; }
    public int LeaseTtlSeconds { get; set; } = 60;
    public int LeaseRenewSeconds { get; set; } = 45;
    public int LeaseTargetUnitsProduct1 { get; set; } = 2; // MVP config: reserve 2 units of product1
    public int DivergenceCheckSeconds { get; set; } = 5;

    private volatile bool _isDivergent;
    private volatile bool _isRecovering;

    private readonly ReplayCircuitBreaker _replayBreaker = new();
    private long _lastReplayStartJitterAtMs;

    // Gap tolerance (storm hardening)
    private long? _gapFirstSeenAtMs;
    private long? _gapExpectedSeq;
    private int _gapSyncAttempts;
    public int GapGraceMs { get; set; } = 15_000;

    // Storm hardening: bounded jitter controls (ms). Deterministic in tests via POSEDGE_JITTER_SEED.
    public int ReplayStartJitterMs { get; set; } = 2_000;
    public int SnapshotRetryJitterMs { get; set; } = 3_000;
    public int SignalRReconnectMaxJitterMs { get; set; } = 2_000;

    private readonly Random _jitterRand;

    public TerminalClient(string apiBase, Guid tenantId, Guid branchId, Guid terminalId, Guid cashSessionId, TerminalReplicaDb replica)
    {
        _apiBase = apiBase.TrimEnd('/');
        _tenantId = tenantId;
        _branchId = branchId;
        _terminalId = terminalId;
        _cashSessionId = cashSessionId;
        _replica = replica;
        _apply = new EventApplyEngine(_replica, _tenantId, _branchId, _terminalId);
        _outbox = new LocalOutboxStore(_replica, _tenantId, _branchId, _terminalId);
        _http.BaseAddress = new Uri(_apiBase + "/");
        _mirror = new ReplayMirrorPublisher(_http, _replica, _tenantId, _branchId, _terminalId);
        _ops = new OpsCommandsPoller(_http, _replica, _tenantId, _branchId, _terminalId);

        _jitterRand = CreateDeterministicRandom(_terminalId);

        if (int.TryParse(Environment.GetEnvironmentVariable("POSEDGE_LEASE_TARGET_UNITS_PRODUCT1"), out var units) && units > 0)
            LeaseTargetUnitsProduct1 = units;
        if (int.TryParse(Environment.GetEnvironmentVariable("POSEDGE_LEASE_TTL_SECONDS"), out var ttl) && ttl > 0)
            LeaseTtlSeconds = ttl;
        if (int.TryParse(Environment.GetEnvironmentVariable("POSEDGE_GAP_GRACE_MS"), out var ggm) && ggm >= 0)
            GapGraceMs = ggm;
        if (int.TryParse(Environment.GetEnvironmentVariable("POSEDGE_DIVERGENCE_CHECK_SECONDS"), out var dcs) && dcs > 0)
            DivergenceCheckSeconds = Math.Clamp(dcs, 1, 60);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        // Staggered reconnect: spread startup to avoid stampede.
        var startupJitterMs = ParseJitterMs("POSEDGE_STARTUP_RECONNECT_JITTER_MS", 0, 5000);
        if (startupJitterMs > 0)
        {
            var d = NextJitterMs(startupJitterMs);
            _replica.AppendReplayLog("reconnect_jitter_ms", d.ToString());
            try { await Task.Delay(d, ct); } catch { }
        }

        _hub = new HubConnectionBuilder()
            .WithUrl(_apiBase + "/hubs/multicaja")
            .WithAutomaticReconnect(new JitterReconnectPolicy(this))
            .Build();

        _hub.On<object>("Server.HelloAck", msg => Console.WriteLine($"[hub] HelloAck {JsonSerializer.Serialize(msg)}"));
        _hub.On<object>("Server.AuthOk", msg => Console.WriteLine($"[hub] AuthOk {JsonSerializer.Serialize(msg)}"));
        _hub.On<object>("Server.HeartbeatAck", msg => Console.WriteLine($"[hub] HeartbeatAck {JsonSerializer.Serialize(msg)}"));
        _hub.On<string>("Server.Event", payloadJson =>
        {
            try
            {
                Console.WriteLine($"[hub] Event {payloadJson}");
                // payload contains {type,tenantId,branchId,seq,at,data...}
                var doc = JsonSerializer.Deserialize<JsonElement>(payloadJson);
                var seq = doc.GetProperty("seq").GetInt64();
                var type = doc.GetProperty("type").GetString() ?? "Unknown";
                var at = doc.TryGetProperty("at", out var atEl) && atEl.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(atEl.GetString()!)
                    : DateTimeOffset.UtcNow;

                var r = _apply.IngestEvent(seq, type, at, payloadJson);
                if (r.Kind == "gap")
                {
                    Console.WriteLine($"[sync] GAP expected={r.GapExpectedSeq} firstSeen={r.GapFirstSeenSeq} lastApplied={r.LastAppliedSeq}");
                    var nowMs = TerminalReplicaDb.NowMs();
                    if (_gapExpectedSeq != r.GapExpectedSeq)
                    {
                        _gapExpectedSeq = r.GapExpectedSeq;
                        _gapFirstSeenAtMs = nowMs;
                        _gapSyncAttempts = 0;
                        _replica.AppendReplayLog("seq_gap_detected", "gap", meta: new { r.GapExpectedSeq, r.GapFirstSeenSeq, r.LastAppliedSeq });
                    }
                }

                if (type == "Lease.Revoked")
                {
                    HandleLeaseRevoked(doc);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[hub] apply error: {ex.Message}");
            }
        });

        await ConnectHubWithRetryAsync(ct);
        try { await SendHelloAsync(ct); }
        catch (Exception ex) { Console.WriteLine($"[hub] Hello error: {ex.Message}"); }

        // Sync-on-connect: pull from lastAppliedSeq to heal missed events.
        _ = Task.Run(() => SyncOnReconnectOnceAsync(ct), ct);
        _ = Task.Run(() => LeaseAutoLoopAsync(ct), ct);
        _ = Task.Run(() => DivergenceLoopAsync(ct), ct);
        _ = Task.Run(() => ReplayMirrorLoopAsync(ct), ct);
        _ = Task.Run(() => OpsCommandsLoopAsync(ct), ct);
    }

    private async Task ReplayMirrorLoopAsync(CancellationToken ct)
    {
        // Publish every ~3s; in storms, publishing helps ops view backlog. Failures are non-fatal.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_hub != null && _hub.State == HubConnectionState.Connected && _mirror != null)
                    await _mirror.PublishOnceAsync(ct);
            }
            catch { }
            try { await Task.Delay(3000, ct); } catch { }
        }
    }

    private async Task OpsCommandsLoopAsync(CancellationToken ct)
    {
        // Poll often so ops actions feel immediate; failures are non-fatal.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_hub != null && _hub.State == HubConnectionState.Connected && _ops != null)
                    await _ops.PollAndApplyOnceAsync(ct);
            }
            catch { }
            try { await Task.Delay(1500, ct); } catch { }
        }
    }

    public TerminalStatusDto GetUxStatus()
    {
        // Source of truth: local terminal_health.state (written by loops)
        try
        {
            using var conn = _replica.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                              SELECT state, last_error
                                FROM terminal_health
                               WHERE tenant_id=$t AND branch_id=$b AND terminal_id=$term
                               LIMIT 1;
                              """;
            cmd.Parameters.AddWithValue("$t", _tenantId.ToString("D"));
            cmd.Parameters.AddWithValue("$b", _branchId.ToString("D"));
            cmd.Parameters.AddWithValue("$term", _terminalId.ToString("D"));
            using var r = cmd.ExecuteReader();
            if (!r.Read())
                return new TerminalStatusDto("offline", "Trabajando sin conexión", false, "Continuar operando. Reintentar conexión.");

            var st = r.GetString(0);
            return st switch
            {
                "online" => new TerminalStatusDto("online", "Sincronizado", false, "Operación normal."),
                "offline" => new TerminalStatusDto("offline", "Trabajando sin conexión", false, "Continuar operando. Se sincronizará al reconectar."),
                "replaying" => new TerminalStatusDto("replaying", "Sincronizando ventas pendientes", false, "Esperar sincronización."),
                "waiting_replay" => new TerminalStatusDto("waiting_replay", "Esperando sincronización", false, "Esperar; el sistema regulará la sincronización."),
                "snapshot_backoff" => new TerminalStatusDto("snapshot_backoff", "Esperando recuperación", true, "No vender; esperar recuperación automática."),
                "degraded" => new TerminalStatusDto("degraded", "Conectividad inestable", false, "Operar con precaución; revisar red/servidor."),
                "recovering_degraded" => new TerminalStatusDto("recovering_degraded", "Conectividad inestable", false, "Esperar; se normalizará."),
                "divergent" => new TerminalStatusDto("divergent", "Recuperando información", true, "No vender; esperar recuperación automática."),
                "recovering" => new TerminalStatusDto("recovering", "Recuperando información", true, "No vender; esperar recuperación automática."),
                "revoked" => new TerminalStatusDto("revoked", "Terminal requiere revisión", true, "Contactar administrador; reactivar terminal."),
                _ => new TerminalStatusDto(st, "Conectividad inestable", false, "Revisar estado de sincronización.")
            };
        }
        catch
        {
            return new TerminalStatusDto("offline", "Trabajando sin conexión", false, "Continuar operando. Reintentar conexión.");
        }
    }

    private static int ParseJitterMs(string envName, int min, int max)
    {
        // Default: jitter disabled unless explicitly enabled, to keep single-terminal behavior snappy.
        if (!int.TryParse(Environment.GetEnvironmentVariable(envName), out var v))
            return 0;
        return Math.Clamp(v, min, max);
    }

    private static Random CreateDeterministicRandom(Guid terminalId)
    {
        // Tests can pin randomness across processes; if missing, default to per-terminal seed.
        var seedEnv = Environment.GetEnvironmentVariable("POSEDGE_JITTER_SEED");
        if (int.TryParse(seedEnv, out var s))
            return new Random(s ^ terminalId.GetHashCode());
        return new Random(terminalId.GetHashCode());
    }

    private int NextJitterMs(int maxExclusiveOrInclusive)
    {
        if (maxExclusiveOrInclusive <= 0) return 0;
        lock (_jitterRand)
        {
            // inclusive upper bound convenience for "0..N"
            return _jitterRand.Next(0, maxExclusiveOrInclusive + 1);
        }
    }

    private sealed class JitterReconnectPolicy : IRetryPolicy
    {
        private readonly TerminalClient _c;
        public JitterReconnectPolicy(TerminalClient c) => _c = c;

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            // Base backoff: 0,2,5,10,... bounded and jittered
            var attempt = retryContext.PreviousRetryCount;
            var baseMs = attempt switch
            {
                0 => 0,
                1 => 250,
                2 => 750,
                3 => 1500,
                4 => 2500,
                _ => 5000
            };
            var jitter = _c.NextJitterMs(_c.SignalRReconnectMaxJitterMs);
            var delay = Math.Min(8000, baseMs + jitter);
            _c._replica.AppendReplayLog("signalr_reconnect_jitter_ms", delay.ToString());
            return TimeSpan.FromMilliseconds(delay);
        }
    }

    private async Task ConnectHubWithRetryAsync(CancellationToken ct)
    {
        if (_hub == null)
            throw new InvalidOperationException("Hub not initialized.");

        await WaitForApiReadyAsync(ct);

        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_hub.State == HubConnectionState.Connected)
                    return;

                await _hub.StartAsync(ct);
                _replica.SetHealth(_tenantId, _branchId, _terminalId, "online");
                Console.WriteLine("[conexión] Conectado al servidor.");
                return;
            }
            catch (Exception ex)
            {
                attempt++;
                _replica.SetHealth(_tenantId, _branchId, _terminalId, "offline", ex.Message);
                var waitMs = Math.Min(15_000, 1500 + attempt * 500);
                Console.WriteLine($"[conexión] Servidor no disponible ({ex.Message}). Reintento en {waitMs / 1000}s...");
                try { await Task.Delay(waitMs, ct); } catch { return; }
            }
        }
    }

    private async Task WaitForApiReadyAsync(CancellationToken ct)
    {
        Console.WriteLine($"[conexión] Esperando servidor en {_apiBase} ...");
        for (var i = 0; i < 90; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var resp = await _http.GetAsync("/health/ready", ct);
                if (resp.IsSuccessStatusCode)
                {
                    Console.WriteLine("[conexión] Servidor listo.");
                    return;
                }
            }
            catch { }

            if (i == 0 || i % 5 == 0)
                Console.WriteLine("[conexión] Iniciando servicios locales... (PosEdgeApi / PosEdgeWorkers)");

            TryStartLocalServices();
            try { await Task.Delay(1000, ct); } catch { return; }
        }

        Console.WriteLine("[conexión] El servidor no respondió. Use Menú Inicio → Reparar instalación.");
    }

    private static void TryStartLocalServices()
    {
        foreach (var svc in new[] { "PosEdgePostgres", "PosEdgeApi", "PosEdgeWorkers", "PosEdgeGuardian" })
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"start \"{svc}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                p?.WaitForExit(3000);
            }
            catch { }
        }
    }

    private async Task SendHelloAsync(CancellationToken ct)
    {
        if (_hub == null) return;
        var mid = Ulid.NewUlidString();
        var env = new McEnvelope
        {
            V = 1,
            T = "Hello",
            Mid = mid,
            Cid = mid,
            TenantId = _tenantId,
            BranchId = _branchId,
            TerminalId = _terminalId,
            SentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Body = new { cap = new { offlineQueue = true } }
        };
        await _hub.InvokeAsync("ClientHello", env, ct);
    }

    public async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_hub != null && _hub.State == HubConnectionState.Connected)
                {
                    var mid = Ulid.NewUlidString();
                    await _hub.InvokeAsync("ClientHeartbeat", new McEnvelope
                    {
                        V = 1,
                        T = "Heartbeat",
                        Mid = mid,
                        Cid = mid,
                        TenantId = _tenantId,
                        BranchId = _branchId,
                        TerminalId = _terminalId,
                        SentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Body = new { }
                    }, ct);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[hb] error: {ex.Message}");
            }
            try { await Task.Delay(2000, ct); } catch { }
        }
    }

    public async Task SubmitSaleCommitAsync(object saleCommitBody, string? requestId = null, CancellationToken ct = default)
    {
        requestId ??= Ulid.NewUlidString();
        if (OfflineMode == OfflineMode.LeasesOnly && ActiveLeaseId == null)
        {
            // Self-healing: after restart or transient renew failure, we may have an active lease persisted locally
            // but the in-memory ActiveLeaseId not set. Recover it so offline sales remain operable.
            ActiveLeaseId = TryLoadActiveLeaseIdFromReplica();
        }

        var leaseId = ActiveLeaseId?.ToString("D");
        var payload = SerializeSaleCommitWithLease(saleCommitBody, OfflineMode == OfflineMode.LeasesOnly ? leaseId : null);

        if (_isRecovering || _isDivergent)
        {
            Console.WriteLine("[health] divergent/recovering: blocking sale submit.");
            _replica.AppendReplayLog("block", "Blocked sale due to divergent/recovering", requestId);
            return;
        }

        // STRICT: do not enqueue offline
        if (OfflineMode == OfflineMode.Strict)
        {
            if (_hub == null || _hub.State != HubConnectionState.Connected)
            {
                Console.WriteLine("[offline] STRICT: no connection; rejecting sale.");
                _replica.AppendReplayLog("reject", "STRICT offline reject", requestId, new { type = "Sale.Commit" });
                return;
            }
        }

        // LEASES_ONLY: require and consume lease locally (atomic).
        if (OfflineMode == OfflineMode.LeasesOnly)
        {
            if (ActiveLeaseId == null)
            {
                Console.WriteLine("[offline] LEASES_ONLY: no lease; rejecting sale.");
                _replica.AppendReplayLog("reject", "LEASES_ONLY reject (no lease)", requestId, new { type = "Sale.Commit" });
                return;
            }
        }

        _outbox.EnqueueSaleCommit(requestId, OfflineMode, payload, leaseId);
        await TryReplayOnceAsync(ct);
    }

    private Guid? TryLoadActiveLeaseIdFromReplica()
    {
        try
        {
            using var conn = _replica.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                              SELECT lease_id
                                FROM leases
                               WHERE status='active'
                                 AND expires_at_ms > $now
                               ORDER BY expires_at_ms DESC
                               LIMIT 1;
                              """;
            cmd.Parameters.AddWithValue("$now", TerminalReplicaDb.NowMs());
            var o = cmd.ExecuteScalar() as string;
            if (Guid.TryParse(o, out var g))
                return g;
        }
        catch { }
        return null;
    }

    public async Task ReplayLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await TryReplayOnceAsync(ct);
            var delayMs = _replayBreaker.GetPaceDelayMs();
            try { await Task.Delay(delayMs, ct); } catch { }
        }
    }

    public async Task SyncLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await SyncOnReconnectOnceAsync(ct);
            try { await Task.Delay(1500, ct); } catch { }
        }
    }

    private async Task LeaseAutoLoopAsync(CancellationToken ct)
    {
        // MVP: keep a small lease for product1 to enable offline sales.
        // Next iteration: compute needs from recent demand + queue depth.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (OfflineMode == OfflineMode.LeasesOnly)
                {
                    // If no lease, request one.
                    if (ActiveLeaseId == null)
                    {
                        var req = new
                        {
                            tenantId = _tenantId,
                            branchId = _branchId,
                            terminalId = _terminalId,
                            requestId = (string?)null,
                            ttlSeconds = LeaseTtlSeconds,
                            lines = new[]
                            {
                                new { productId = Guid.Parse("33333333-3333-3333-3333-333333333333"), qty = (decimal)LeaseTargetUnitsProduct1 }
                            }
                        };
                        var resp = await _http.PostAsJsonAsync("/v1/leases/request", req, ct);
                        resp.EnsureSuccessStatusCode();
                        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                        if (json.TryGetProperty("ok", out var okEl) && okEl.GetBoolean())
                        {
                            var leaseId = json.GetProperty("leaseId").GetGuid();
                            ActiveLeaseId = leaseId;
                            PersistLeaseLocal(json);
                            Console.WriteLine($"[lease] granted {leaseId}");
                        }
                        else
                        {
                            var code = json.TryGetProperty("code", out var cEl) ? cEl.GetString() : "ERR";
                            Console.WriteLine($"[lease] request failed code={code}");
                        }
                    }
                    else
                    {
                        // Renew periodically if connected
                        if (_hub != null && _hub.State == HubConnectionState.Connected)
                        {
                            var req = new
                            {
                                tenantId = _tenantId,
                                branchId = _branchId,
                                terminalId = _terminalId,
                                leaseId = ActiveLeaseId.Value,
                                requestId = (string?)null,
                                extendSeconds = LeaseRenewSeconds
                            };
                            var resp = await _http.PostAsJsonAsync("/v1/leases/renew", req, ct);
                            resp.EnsureSuccessStatusCode();
                            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                            var ok = json.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                            if (!ok)
                            {
                                var code = json.TryGetProperty("code", out var cEl) ? cEl.GetString() : "ERR";
                                Console.WriteLine($"[lease] renew rejected code={code}; clearing lease");
                                ActiveLeaseId = null;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[lease] auto error: {ex.Message}");
            }

            try { await Task.Delay(2000, ct); } catch { }
        }
    }

    private void PersistLeaseLocal(JsonElement leaseResponse)
    {
        // Persist into replica sqlite (leases + lease_lines) best-effort.
        // This is crucial for offline enforcement.
        if (!leaseResponse.TryGetProperty("leaseId", out var idEl)) return;
        var leaseId = idEl.GetGuid().ToString("D");
        var expiresAt = leaseResponse.TryGetProperty("expiresAt", out var exEl) && exEl.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(exEl.GetString()!).ToUnixTimeMilliseconds()
            : TerminalReplicaDb.NowMs() + 60_000;

        using var conn = _replica.OpenConnection();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                              INSERT INTO leases(lease_id, tenant_id, branch_id, terminal_id, created_at_ms, expires_at_ms, status)
                              VALUES ($lid, $t, $b, $term, $now, $exp, 'active')
                              ON CONFLICT(lease_id) DO UPDATE SET
                                expires_at_ms=excluded.expires_at_ms,
                                status='active';
                              """;
            cmd.Parameters.AddWithValue("$lid", leaseId);
            cmd.Parameters.AddWithValue("$t", _tenantId.ToString("D"));
            cmd.Parameters.AddWithValue("$b", _branchId.ToString("D"));
            cmd.Parameters.AddWithValue("$term", _terminalId.ToString("D"));
            cmd.Parameters.AddWithValue("$now", TerminalReplicaDb.NowMs());
            cmd.Parameters.AddWithValue("$exp", expiresAt);
            cmd.ExecuteNonQuery();
        }

        if (leaseResponse.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in lines.EnumerateArray())
            {
                var pid = l.GetProperty("productId").GetGuid().ToString("D");
                var alloc = l.GetProperty("qtyAllocated").GetDouble();
                var used = l.GetProperty("qtyUsed").GetDouble();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                                  INSERT INTO lease_lines(lease_id, product_id, qty_allocated, qty_used)
                                  VALUES ($lid, $pid, $a, $u)
                                  ON CONFLICT(lease_id, product_id) DO UPDATE SET
                                    qty_allocated=excluded.qty_allocated,
                                    qty_used=excluded.qty_used;
                                  """;
                cmd.Parameters.AddWithValue("$lid", leaseId);
                cmd.Parameters.AddWithValue("$pid", pid);
                cmd.Parameters.AddWithValue("$a", alloc);
                cmd.Parameters.AddWithValue("$u", used);
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    private async Task SyncOnReconnectOnceAsync(CancellationToken ct)
    {
        try
        {
            var from = _apply.LastAppliedSeq;
            var url = $"/v1/sync?tenantId={Uri.EscapeDataString(_tenantId.ToString())}&branchId={Uri.EscapeDataString(_branchId.ToString())}&fromSeq={from}&limit=500";
            var resp = await _http.GetFromJsonAsync<JsonElement>(url, ct);
            if (resp.ValueKind != JsonValueKind.Object) return;
            if (!resp.TryGetProperty("events", out var eventsEl) || eventsEl.ValueKind != JsonValueKind.Array) return;
            var rows = new List<ServerEventRow>();
            long? minSeenSeq = null;
            var maxSeenSeq = from;
            foreach (var e in eventsEl.EnumerateArray())
            {
                var seq = e.GetProperty("seq").GetInt64();
                if (minSeenSeq == null || seq < minSeenSeq.Value) minSeenSeq = seq;
                if (seq > maxSeenSeq) maxSeenSeq = seq;
                var type = e.GetProperty("type").GetString() ?? "Unknown";
                var at = e.TryGetProperty("at", out var atEl) && atEl.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(atEl.GetString()!)
                    : DateTimeOffset.UtcNow;
                var dataJson = e.GetProperty("dataJson").GetString() ?? "{}";
                // Store as a synthetic payload that matches hub payload shape for uniformity:
                var payloadJson = JsonSerializer.Serialize(new
                {
                    type,
                    tenantId = _tenantId,
                    branchId = _branchId,
                    seq,
                    at,
                    data = JsonSerializer.Deserialize<object>(dataJson)
                });
                rows.Add(new ServerEventRow(seq, type, at, payloadJson));
            }
            if (rows.Count > 0)
            {
                var r = _apply.ApplyBatch(rows);
                Console.WriteLine($"[sync] pull applied={r.AppliedCount} last={r.LastAppliedSeq}");

                // IMPORTANT: server event_log.seq is identity and can have holes (sequence increments even on rollback).
                // If the server returned events (minSeenSeq > expected) but we couldn't advance, bump the checkpoint
                // to just before minSeenSeq so the pending_events can drain contiguously.
                var expected = from + 1;
                if (r.AppliedCount == 0 && minSeenSeq.HasValue && minSeenSeq.Value > expected)
                {
                    var current = _apply.LastAppliedSeq; // re-read to avoid regressions
                    var bumpTo = minSeenSeq.Value - 1;
                    if (bumpTo > current)
                    {
                        _replica.SetLastAppliedSeq(_tenantId, _branchId, _terminalId, bumpTo);
                        _replica.AppendReplayLog("seq_hole_bump", $"Bumped checkpoint over seq hole to {bumpTo}", meta: new { from, expected, minSeenSeq, maxSeenSeq });
                        Console.WriteLine($"[sync] bumped checkpoint to {bumpTo} (identity seq hole)");
                        // Drain after bump to apply the newly-contiguous pending events.
                        var r2 = _apply.ApplyBatch(Array.Empty<ServerEventRow>());
                        if (r2.AppliedCount > 0)
                            Console.WriteLine($"[sync] drain applied={r2.AppliedCount} last={r2.LastAppliedSeq}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[sync] error: {ex.Message}");
        }
    }

    private async Task TryReplayOnceAsync(CancellationToken ct)
    {
        // Always run inflight recovery first, even when replay is paused/degraded.
        // Otherwise inflight can remain stuck indefinitely under breaker/ops pause.
        var recovered = _outbox.RecoverStuckInFlight(TerminalReplicaDb.NowMs() - 20_000);
        if (recovered > 0)
            _replica.AppendReplayLog("replay_reclaimed", recovered.ToString(), meta: new { recovered });

        if (_isRecovering || _isDivergent)
            return;

        // Ops pause: if terminal_health.state == paused, stop replay but remain operational offline.
        try
        {
            using var conn = _replica.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                              SELECT state
                                FROM terminal_health
                               WHERE tenant_id=$t AND branch_id=$b AND terminal_id=$term
                               LIMIT 1;
                              """;
            cmd.Parameters.AddWithValue("$t", _tenantId.ToString("D"));
            cmd.Parameters.AddWithValue("$b", _branchId.ToString("D"));
            cmd.Parameters.AddWithValue("$term", _terminalId.ToString("D"));
            var st = cmd.ExecuteScalar() as string;
            if (string.Equals(st, "paused", StringComparison.OrdinalIgnoreCase))
                return;
        }
        catch { }

        if (_replayBreaker.ShouldPause())
        {
            _replica.AppendReplayLog("replay_paused", "circuit_breaker_open");
            _replica.SetHealth(_tenantId, _branchId, _terminalId, "degraded", "replay_paused");
            return;
        }

        var nowMs = TerminalReplicaDb.NowMs();
        var batchSize = int.TryParse(Environment.GetEnvironmentVariable("POSEDGE_REPLAY_BATCH_SIZE"), out var bs) ? Math.Clamp(bs, 1, 50) : 10;
        var batch = _outbox.DequeueBatch(batchSize, nowMs);
        if (batch.Count == 0)
            return;

        // Replay start jitter (spread out after reconnect / storms) — once per window to avoid slowing steady drain.
        var replayJitter = ParseJitterMs("POSEDGE_REPLAY_START_JITTER_MS", 0, 5000);
        if (replayJitter > 0 && nowMs - _lastReplayStartJitterAtMs > 10_000)
        {
            _lastReplayStartJitterAtMs = nowMs;
            var d = NextJitterMs(Math.Min(replayJitter, ReplayStartJitterMs));
            if (d > 0)
            {
                _replica.SetHealth(_tenantId, _branchId, _terminalId, "recovering_degraded", "replay_start_jitter", new { delayMs = d });
                _replica.AppendReplayLog("replay_start_jitter_ms", d.ToString());
                try { await Task.Delay(d, ct); } catch { }
            }
        }

        // Global admission: claim a replay slot before hammering API/DB.
        var slot = await TryClaimAdmissionSlotAsync("replay", ttlSeconds: 12, ct);
        if (!slot.Ok)
        {
            _replica.AppendReplayLog("replay_backpressure", $"{slot.Code}:{slot.Message}");
            _replica.SetHealth(_tenantId, _branchId, _terminalId, "waiting_replay", slot.Code, new { slot.RetryAfterMs });
            var wait = slot.RetryAfterMs ?? (750 + NextJitterMs(1000));
            try { await Task.Delay(wait, ct); } catch { }
            return;
        }

        try
        {
            // Replay batching: if we have >1 item, reduce roundtrips with commit-batch.
            if (batch.Count > 1)
            {
                await ReplayBatchHttpAsync(batch, ct);
                return;
            }

            foreach (var item in batch)
            {
                _outbox.MarkInFlight(item.RequestId);
                try
                {
                    if (item.Type == "Sale.Commit")
                    {
                        // Sale.Commit: send HTTP command (MVP). In future: hub commands batch.
                        var doc = JsonSerializer.Deserialize<JsonElement>(item.PayloadJson);
                        using var resp = await _http.PostAsJsonAsync("/v1/sales/commit", doc, ct);
                        resp.EnsureSuccessStatusCode();
                        var r = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                        var ok = r.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                        if (ok)
                        {
                            Console.WriteLine($"[replay] committed rid={item.RequestId} resp={r}");
                            _outbox.MarkCommitted(item.RequestId);
                            _replayBreaker.OnSuccess(() =>
                            {
                                _replica.AppendReplayLog("replay_resumed", "ok");
                                _replica.SetHealth(_tenantId, _branchId, _terminalId, "online");
                            });
                            _replica.SetHealth(_tenantId, _branchId, _terminalId, "online", null, new { lastReplayAtMs = TerminalReplicaDb.NowMs() });
                        }
                        else
                        {
                            var code = r.TryGetProperty("code", out var cEl) ? cEl.GetString() : "ERR";
                            var msg = r.TryGetProperty("message", out var mEl) ? mEl.GetString() : "error";
                            Console.WriteLine($"[replay] failed rid={item.RequestId} code={code} msg={msg}");
                            var retryable = code is "TEMP_UNAVAILABLE" or "DB_ERROR";
                            _outbox.MarkRejected(item.RequestId, code ?? "ERR", msg ?? "error", retryable: retryable,
                                attemptCount: item.AttemptCount + 1);

                            if (code is "LEASE_REVOKED" or "LEASE_EXPIRED" or "INVALID_TERMINAL")
                            {
                                // If the server rejects due to lease/terminal consistency, dead-letter all pending sales for that lease.
                                // We try to infer leaseId from payload.
                                if (TryExtractLeaseId(item.PayloadJson, out var lid))
                                {
                                    _outbox.DeadLetterByLease(lid!, code!, msg ?? "rejected");
                                    if (ActiveLeaseId?.ToString("D") == lid)
                                        ActiveLeaseId = null;
                                    _replica.SetHealth(_tenantId, _branchId, _terminalId, "revoked", $"{code}:{msg}");
                                }
                            }

                            if (retryable)
                            {
                                _replayBreaker.OnFailure(() =>
                                {
                                    _replica.AppendReplayLog("postgres_unavailable", $"{code}:{msg}");
                                    _replica.AppendReplayLog("circuit_breaker_open", "replay");
                                    _replica.SetHealth(_tenantId, _branchId, _terminalId, "degraded", $"{code}:{msg}");
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[replay] retry rid={item.RequestId} err={ex.Message}");
                    _outbox.MarkRejected(item.RequestId, "TEMP_UNAVAILABLE", ex.Message, retryable: true,
                        attemptCount: item.AttemptCount + 1);
                    _replayBreaker.OnFailure(() =>
                    {
                        _replica.AppendReplayLog("postgres_unavailable", ex.Message);
                        _replica.AppendReplayLog("circuit_breaker_open", "replay");
                        _replica.SetHealth(_tenantId, _branchId, _terminalId, "degraded", ex.Message);
                    });
                }
                // Pace between items (adaptive-ish). This protects server even with slots.
                var pace = _replayBreaker.GetPaceDelayMs();
                _replica.AppendReplayLog("replay_backoff_ms", pace.ToString(), item.RequestId);
                try { await Task.Delay(pace, ct); } catch { }
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(slot.Token) && slot.Token != "ADMISSION_BYPASS")
                await ReleaseAdmissionSlotAsync("replay", slot.Token!, ct);
        }
    }

    private sealed record AdmissionResult(bool Ok, string Code, string? Message, string? Token, int? SlotId, int? RetryAfterMs);

    private async Task<AdmissionResult> TryClaimAdmissionSlotAsync(string kind, int ttlSeconds, CancellationToken ct)
    {
        try
        {
            var req = new
            {
                tenantId = _tenantId,
                branchId = _branchId,
                terminalId = _terminalId,
                kind,
                ttlSeconds,
                requestId = Ulid.NewUlidString()
            };
            using var resp = await _http.PostAsJsonAsync("/v1/admission/claim", req, ct);
            resp.EnsureSuccessStatusCode();
            var r = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var ok = r.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            var code = r.TryGetProperty("code", out var cEl) ? cEl.GetString() : "ERR";
            var msg = r.TryGetProperty("message", out var mEl) ? mEl.GetString() : null;
            var token = r.TryGetProperty("token", out var tEl) ? tEl.GetString() : null;
            var slotId = r.TryGetProperty("slotId", out var sEl) && sEl.ValueKind == JsonValueKind.Number ? sEl.GetInt32() : (int?)null;
            var ra = r.TryGetProperty("retryAfterMs", out var raEl) && raEl.ValueKind == JsonValueKind.Number ? raEl.GetInt32() : (int?)null;
            return new AdmissionResult(ok, code ?? "ERR", msg, token, slotId, ra);
        }
        catch (Exception ex)
        {
            // Treat admission endpoint failure as transient infra; don't block replay forever.
            _replica.AppendReplayLog("replay_backpressure", "admission_error", meta: new { ex = ex.Message, kind });
            return new AdmissionResult(true, "OK", null, "ADMISSION_BYPASS", null, null);
        }
    }

    private async Task ReleaseAdmissionSlotAsync(string kind, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        try
        {
            var req = new { kind, token };
            using var resp = await _http.PostAsJsonAsync("/v1/admission/release", req, ct);
            // best-effort
            _ = resp;
        }
        catch { }
    }

    private async Task ReplayBatchHttpAsync(List<LocalOutboxItem> items, CancellationToken ct)
    {
        // All items are inflight already by caller.
        // Mark inflight now so crash recovery can requeue deterministically.
        foreach (var it in items)
            _outbox.MarkInFlight(it.RequestId);

        try
        {
            var docs = items.Select(i => JsonSerializer.Deserialize<JsonElement>(i.PayloadJson)).ToList();
            using var resp = await _http.PostAsJsonAsync("/v1/replay/sales/commit-batch", docs, ct);
            resp.EnsureSuccessStatusCode();
            var r = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("batch response inválida");

            var byRid = results.EnumerateArray()
                .Where(x => x.TryGetProperty("requestId", out _))
                .ToDictionary(x => x.GetProperty("requestId").GetString()!, x => x, StringComparer.OrdinalIgnoreCase);

            foreach (var it in items)
            {
                if (!byRid.TryGetValue(it.RequestId, out var one))
                    throw new InvalidOperationException($"batch missing requestId={it.RequestId}");

                var ok = one.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                if (ok)
                {
                    _outbox.MarkCommitted(it.RequestId);
                    _replayBreaker.OnSuccess(() =>
                    {
                        _replica.AppendReplayLog("replay_resumed", "ok");
                        _replica.SetHealth(_tenantId, _branchId, _terminalId, "online");
                    });
                }
                else
                {
                    var code = one.TryGetProperty("code", out var cEl) ? cEl.GetString() : "ERR";
                    var msg = one.TryGetProperty("message", out var mEl) ? mEl.GetString() : "error";
                    var retryable = code is "TEMP_UNAVAILABLE" or "DB_ERROR";
                    _outbox.MarkRejected(it.RequestId, code ?? "ERR", msg ?? "error", retryable: retryable,
                        attemptCount: it.AttemptCount + 1);

                    if (code is "LEASE_REVOKED" or "LEASE_EXPIRED" or "INVALID_TERMINAL")
                    {
                        if (TryExtractLeaseId(it.PayloadJson, out var lid))
                        {
                            _outbox.DeadLetterByLease(lid!, code!, msg ?? "rejected");
                            if (ActiveLeaseId?.ToString("D") == lid)
                                ActiveLeaseId = null;
                            _replica.SetHealth(_tenantId, _branchId, _terminalId, "revoked", $"{code}:{msg}");
                        }
                    }

                    if (retryable)
                    {
                        _replayBreaker.OnFailure(() =>
                        {
                            _replica.AppendReplayLog("postgres_unavailable", $"{code}:{msg}");
                            _replica.AppendReplayLog("circuit_breaker_open", "replay");
                            _replica.SetHealth(_tenantId, _branchId, _terminalId, "degraded", $"{code}:{msg}");
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Treat whole batch as transient; explicitly move items out of inflight so they can retry deterministically.
            _replica.AppendReplayLog("replay_retry_scheduled", ex.Message);
            foreach (var it in items)
            {
                try
                {
                    _outbox.MarkRejected(it.RequestId, "TEMP_UNAVAILABLE", ex.Message, retryable: true, attemptCount: it.AttemptCount + 1);
                }
                catch { }
            }
            _replayBreaker.OnFailure(() =>
            {
                _replica.AppendReplayLog("postgres_unavailable", ex.Message);
                _replica.AppendReplayLog("circuit_breaker_open", "replay");
                _replica.SetHealth(_tenantId, _branchId, _terminalId, "degraded", ex.Message);
            });
        }
        finally
        {
            var pace = _replayBreaker.GetPaceDelayMs();
            _replica.AppendReplayLog("replay_backoff_ms", pace.ToString());
            try { await Task.Delay(pace, ct); } catch { }
        }
    }

    private void HandleLeaseRevoked(JsonElement envelope)
    {
        try
        {
            if (!envelope.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
            var leaseId = data.TryGetProperty("leaseId", out var lidEl) ? lidEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(leaseId)) return;

            Console.WriteLine($"[lease] revoked {leaseId}");
            // Mark lease locally revoked + dead-letter affected sales.
            _outbox.DeadLetterByLease(leaseId!, "LEASE_REVOKED", "Lease revoked by server");

            if (ActiveLeaseId?.ToString("D") == leaseId)
                ActiveLeaseId = null;

            _replica.SetHealth(_tenantId, _branchId, _terminalId, "revoked", "LEASE_REVOKED");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[lease] revoke handling error: {ex.Message}");
        }
    }

    private async Task DivergenceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAndRecoverDivergenceOnce(ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[divergence] loop error: {ex.Message}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(DivergenceCheckSeconds), ct); } catch { }
        }
    }

    private async Task CheckAndRecoverDivergenceOnce(CancellationToken ct)
    {
        if (_hub == null || _hub.State != HubConnectionState.Connected)
            return;
        if (_isRecovering)
            return;

        // Test hook: allow forcing divergence for deterministic integration tests.
        if (Environment.GetEnvironmentVariable("POSEDGE_TEST_FORCE_DIVERGENT") == "1")
        {
            EnterDivergent("TEST_FORCE", new { });
        }

        // 1) Detect irreparable gaps (we keep it simple: any gap lasting without healing => force snapshot)
        var gap = _replica.GetGapInfo(_tenantId, _branchId, _terminalId);
        if (gap.GapExpectedSeq.HasValue)
        {
            var nowMs = TerminalReplicaDb.NowMs();
            _gapExpectedSeq ??= gap.GapExpectedSeq;
            _gapFirstSeenAtMs ??= nowMs;

            // Try to heal via pull sync a few times before escalating to full resync.
            if (_gapSyncAttempts < 3)
            {
                _gapSyncAttempts++;
                _replica.SetHealth(_tenantId, _branchId, _terminalId, "degraded", "seq_gap_detected",
                    new { gap.LastAppliedSeq, gap.MinPendingSeq, gap.GapExpectedSeq, attempts = _gapSyncAttempts });
                await SyncOnReconnectOnceAsync(ct);
                return;
            }

            // Grace window to avoid false divergence under storm/out-of-order delivery.
            if (nowMs - _gapFirstSeenAtMs.Value < GapGraceMs)
            {
                _replica.SetHealth(_tenantId, _branchId, _terminalId, "degraded", "seq_gap_grace",
                    new { gap.LastAppliedSeq, gap.MinPendingSeq, gap.GapExpectedSeq, firstSeenAtMs = _gapFirstSeenAtMs });
                return;
            }

            _replica.AppendReplayLog("seq_gap_escalated", "divergent", meta: new { gap.LastAppliedSeq, gap.MinPendingSeq, gap.GapExpectedSeq });
            EnterDivergent("SEQ_GAP", new { gap.LastAppliedSeq, gap.MinPendingSeq, gap.GapExpectedSeq });
        }
        else
        {
            if (_gapFirstSeenAtMs.HasValue)
                _replica.AppendReplayLog("seq_gap_healed", "ok", meta: new { expected = _gapExpectedSeq });
            _gapFirstSeenAtMs = null;
            _gapExpectedSeq = null;
            _gapSyncAttempts = 0;
        }

        // 2) Hash mismatch detection: compare local vs server snapshot hashes (hashOnly=true)
        if (!_isDivergent)
        {
            var url = $"/v1/snapshot?tenantId={Uri.EscapeDataString(_tenantId.ToString())}&branchId={Uri.EscapeDataString(_branchId.ToString())}&hashOnly=true&includeLeases=true";
            var server = await _http.GetFromJsonAsync<JsonElement>(url, ct);
            if (server.ValueKind == JsonValueKind.Object && server.TryGetProperty("ok", out var okEl) && okEl.GetBoolean())
            {
                var serverBaseSeq = server.TryGetProperty("baseSeq", out var bsEl) && bsEl.ValueKind == JsonValueKind.Number
                    ? bsEl.GetInt64()
                    : 0;
                // Only compare hashes when we're caught up to server base seq,
                // otherwise mismatch is expected and sync loop should heal first.
                if (_apply.LastAppliedSeq < serverBaseSeq)
                    return;

                var local = _replica.ComputeLocalHashes(serverBaseSeq);
                var serverHash = server.GetProperty("snapshotHashNoLeases").GetString() ?? "";
                if (!string.Equals(serverHash, local.SnapshotHashNoLeases, StringComparison.OrdinalIgnoreCase))
                {
                    EnterDivergent("HASH_MISMATCH", new { local = local.SnapshotHashNoLeases, server = serverHash, baseSeq = serverBaseSeq });
                }
            }
        }

        if (_isDivergent)
        {
            await RecoverViaSnapshotAsync(ct);
        }
    }

    private void EnterDivergent(string reason, object meta)
    {
        if (_isDivergent) return;
        _isDivergent = true;
        ActiveLeaseId = null;
        _replica.SetHealth(_tenantId, _branchId, _terminalId, "divergent", reason, meta);
        _replica.AppendReplayLog("divergence_detected", reason, meta: meta);
        Console.WriteLine($"[health] DIVERGENT reason={reason}");
    }

    private async Task RecoverViaSnapshotAsync(CancellationToken ct)
    {
        if (_isRecovering) return;
        _isRecovering = true;
        var holdMs = int.TryParse(Environment.GetEnvironmentVariable("POSEDGE_TEST_DIVERGENT_HOLD_MS"), out var hm) ? hm : 0;
        if (holdMs > 0)
            await Task.Delay(holdMs, ct);

        // Snapshot storm protection: claim snapshot slot so only N terminals recover concurrently.
        var snapSlot = await TryClaimAdmissionSlotAsync("snapshot", ttlSeconds: 20, ct);
        if (!snapSlot.Ok)
        {
            _replica.AppendReplayLog("snapshot_backpressure", $"{snapSlot.Code}:{snapSlot.Message}");
            _replica.SetHealth(_tenantId, _branchId, _terminalId, "snapshot_backoff", snapSlot.Code, new { snapSlot.RetryAfterMs });
            var wait = (snapSlot.RetryAfterMs ?? (1000 + NextJitterMs(SnapshotRetryJitterMs)));
            try { await Task.Delay(wait, ct); } catch { }
            _isRecovering = false;
            return;
        }

        _replica.SetHealth(_tenantId, _branchId, _terminalId, "recovering", "snapshot");
        _replica.AppendReplayLog("snapshot_started", "starting");
        Console.WriteLine("[health] recovering via snapshot...");

        try
        {
            var url = $"/v1/snapshot?tenantId={Uri.EscapeDataString(_tenantId.ToString())}&branchId={Uri.EscapeDataString(_branchId.ToString())}&hashOnly=false&includeLeases=true";
            var snap = await _http.GetFromJsonAsync<JsonElement>(url, ct);
            _replica.ApplySnapshotAtomic(_tenantId, _branchId, _terminalId, snap);

            _isDivergent = false;
            _replica.SetHealth(_tenantId, _branchId, _terminalId, "online", null, new { recovered = true, lastSnapshotAtMs = TerminalReplicaDb.NowMs() });
            _replica.AppendReplayLog("snapshot_completed", "ok");
            _replica.AppendReplayLog("replay_resumed", "ok");
            Console.WriteLine("[health] snapshot applied; back online.");
        }
        catch (Exception ex)
        {
            // Keep old state intact (ApplySnapshotAtomic is transactional)
            _replica.SetHealth(_tenantId, _branchId, _terminalId, "divergent", ex.Message);
            _replica.AppendReplayLog("snapshot_failed", ex.Message);
            Console.WriteLine($"[health] snapshot recovery failed: {ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(snapSlot.Token) && snapSlot.Token != "ADMISSION_BYPASS")
                await ReleaseAdmissionSlotAsync("snapshot", snapSlot.Token!, ct);
            _isRecovering = false;
        }
    }

    private static bool TryExtractLeaseId(string payloadJson, out string? leaseId)
    {
        leaseId = null;
        try
        {
            var root = JsonSerializer.Deserialize<JsonElement>(payloadJson);
            if (root.TryGetProperty("leaseId", out var lidEl) && lidEl.ValueKind == JsonValueKind.String)
            {
                leaseId = lidEl.GetString();
                return !string.IsNullOrWhiteSpace(leaseId);
            }
        }
        catch { }
        return false;
    }

    private static string SerializeSaleCommitWithLease(object saleCommitBody, string? leaseId)
    {
        if (string.IsNullOrWhiteSpace(leaseId))
            return JsonSerializer.Serialize(saleCommitBody);

        var json = JsonSerializer.Serialize(saleCommitBody);
        var node = JsonNode.Parse(json) as JsonObject;
        if (node == null) return json;
        node["leaseId"] = leaseId;
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}

internal sealed class ReplayCircuitBreaker
{
    private enum State { Closed, Open, HalfOpen }
    private State _state = State.Closed;
    private int _failures;
    private long _openUntilMs;
    private long _lastAttemptMs;

    private const int FailureThreshold = 3;
    private const int OpenCooldownMs = 3_000;
    private const int HalfOpenProbeMinIntervalMs = 1_000;

    public bool ShouldPause()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_state == State.Open)
        {
            if (now >= _openUntilMs)
                _state = State.HalfOpen;
            else
                return true;
        }

        if (_state == State.HalfOpen)
        {
            if (now - _lastAttemptMs < HalfOpenProbeMinIntervalMs)
                return true;
        }

        _lastAttemptMs = now;
        return false;
    }

    public void OnFailure(Action onOpen)
    {
        _failures++;
        if (_state == State.Closed && _failures >= FailureThreshold)
        {
            _state = State.Open;
            _openUntilMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + OpenCooldownMs + JitterMs(500);
            onOpen();
            return;
        }
        if (_state == State.HalfOpen)
        {
            _state = State.Open;
            _openUntilMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + OpenCooldownMs + JitterMs(500);
            onOpen();
        }
    }

    public void OnSuccess(Action onClose)
    {
        _failures = 0;
        if (_state != State.Closed)
        {
            _state = State.Closed;
            onClose();
        }
    }

    public int GetPaceDelayMs()
    {
        if (_state == State.Open) return 750 + JitterMs(250);
        if (_state == State.HalfOpen) return 500 + JitterMs(250);
        return 250 + JitterMs(150);
    }

    private static int JitterMs(int max) => max <= 0 ? 0 : Random.Shared.Next(0, max);
}

