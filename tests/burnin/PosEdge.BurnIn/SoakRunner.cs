using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PosEdge.IntegrationTests;
using PosEdge.ChaosTests;


namespace PosEdge.BurnIn;

public static class SoakRunner
{
    public static async Task<int> Main(string[] args)
    {
        var preset = args.Length > 0 ? args[0] : "SMOKE";

        if (string.Equals(preset, "TRIAGE", StringComparison.OrdinalIgnoreCase))
        {
            var runDir = args.Length > 1 ? args[1] : "";
            if (string.IsNullOrWhiteSpace(runDir) || !Directory.Exists(runDir))
            {
                Console.WriteLine("Usage: TRIAGE <runDir>");
                return 2;
            }

            await RunTriageAsync(runDir, CancellationToken.None);
            return 0;
        }
        var terminals = args.Length > 1 && int.TryParse(args[1], out var t) ? t : 10;
        var duration = preset.ToUpperInvariant() switch
        {
            "SMOKE" => TimeSpan.FromMinutes(30),
            "SHORT" => TimeSpan.FromHours(6),
            "MEDIUM" => TimeSpan.FromHours(12),
            "LONG" => TimeSpan.FromHours(24),
            _ when int.TryParse(preset, out var minutes) => TimeSpan.FromMinutes(minutes),
            _ => TimeSpan.FromMinutes(30)
        };
        using var cts = new CancellationTokenSource(duration + TimeSpan.FromMinutes(10));

        await using var h = new ProcessHarness();
        var port = ProcessHarness.GetFreeTcpPort();
        var apiUrl = $"http://localhost:{port}";
        var repoRoot = ProcessHarness.FindRepoRoot(AppContext.BaseDirectory);
        var apiProj = Path.Combine(repoRoot, "src", "PosEdge.Api");
        var terminalProj = Path.Combine(repoRoot, "src", "PosEdge.Terminal");

        var apiEnv = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["POSEDGE_OPS_METRICS_SECONDS"] = "5",
            ["POSEDGE_RECON_SCAN_SECONDS"] = "15",
            // Critical for chaos runs: restarts must NOT re-seed deterministic counters (e.g. ticket=1000),
            // otherwise server_ticket uniqueness will break and invalidate replay stability results.
            ["Edge:AutoSeed"] = "false"
        };

        var api = await h.StartDotnetAsync(apiProj, $"run -c Release --no-build --urls {apiUrl}", apiEnv);
        _ = api;
        await h.WaitForHttpOkAsync(apiUrl, "/health/ready", TimeSpan.FromSeconds(45));

        // Ensure inventory is healthy to avoid flakiness from previous runs.
        // Estimate worst-case units consumed by the burn-in sales loop: 1 unit per tick.
        var estSales = (decimal)(duration.TotalMilliseconds / 150.0);
        var leaseTarget = 25m;
        var needed = Math.Ceiling(estSales + (terminals * leaseTarget) + 1000m);
        await ResetInventoryAsync(onHand: Math.Max(500m, needed), reserved: 0m, cts.Token);

        var termEnvBase = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
            // Burn-in: keep enough lease headroom so the run measures replay/infra stability, not immediate lease exhaustion.
            ["POSEDGE_LEASE_TARGET_UNITS_PRODUCT1"] = "25",
            ["POSEDGE_LEASE_TTL_SECONDS"] = "240",
            ["POSEDGE_STARTUP_RECONNECT_JITTER_MS"] = "5000",
            ["POSEDGE_REPLAY_START_JITTER_MS"] = "5000",
            ["POSEDGE_REPLAY_BATCH_SIZE"] = "15",
            ["POSEDGE_JITTER_SEED"] = "777"
        };

        var procs = new List<Process>();
        var terminalsInfo = new List<TerminalInfo>();
        for (var i = 0; i < terminals; i++)
        {
            var replica = Path.Combine(Path.GetTempPath(), $"posedg-burnin-{i}-{Guid.NewGuid():N}.sqlite");
            // Use overrides so each terminal is unique (tests already provision cash sessions; burn-in uses seeded A/B only for now).
            var termId = Guid.NewGuid();
            var cashId = Guid.NewGuid();

            // Provision minimal terminal+cash session in DB via direct SQL to avoid large dependencies.
            await ProvisionTerminalAndCashAsync(termId, cashId, cts.Token);

            var env = new Dictionary<string, string>(termEnvBase);
            var p = await h.StartDotnetAsync(terminalProj, $"run -c Release --no-build -- {apiUrl} x \"{replica}\" {termId:D} {cashId:D}", env);
            procs.Add(p);
            terminalsInfo.Add(new TerminalInfo(termId, cashId, replica, p));
        }

        // Wait for each terminal to acquire a lease before starting load.
        // This avoids counting "rejected because no lease yet" as lost sales.
        await Task.WhenAll(terminalsInfo.Select(ti => WaitForActiveLeaseAsync(ti.ReplicaPath, TimeSpan.FromSeconds(90), cts.Token)));

        var outDir = Path.Combine(repoRoot, "tests", "burnin", "out", "run-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(outDir);
        var timelinePath = Path.Combine(outDir, "burnin-timeline.log");
        var chaosPath = Path.Combine(outDir, "chaos-events.log");

        var startAt = DateTimeOffset.UtcNow;
        var endAt = startAt + duration;
        var rnd = new Random(123);
        var sentSales = 0;
        var actions = new BurnInCounters();
        using var http = new HttpClient { BaseAddress = new Uri(apiUrl) };
        var pg = await PostgresControllerFactory.DetectAsync(cts.Token);
        if (pg == null)
            AppendLine(chaosPath, $"{DateTimeOffset.UtcNow:O} postgres_controller=none");
        else
            AppendLine(chaosPath, $"{DateTimeOffset.UtcNow:O} postgres_controller={pg.Kind}");

        // Burn-in loop: mixed sales load + infra chaos + ops chaos.
        while (DateTimeOffset.UtcNow < endAt && !cts.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;

            // SALES LOAD: enqueue constant sales
            {
                var p = procs[rnd.Next(procs.Count)];
                await p.StandardInput.WriteLineAsync("sale");
                await p.StandardInput.FlushAsync();
                sentSales++;
                actions.SalesEnqueued++;
                AppendLine(timelinePath, $"{now:O} sale_enqueued");
            }

            // Occasionally restart API port-level (soft chaos)
            if (sentSales % 200 == 0)
            {
                ProcessHarness.Kill(api);
                await Task.Delay(1000, cts.Token);
                api = await h.StartDotnetAsync(apiProj, $"run -c Release --no-build --urls {apiUrl}", apiEnv);
                await h.WaitForHttpOkAsync(apiUrl, "/health/ready", TimeSpan.FromSeconds(45));
                actions.ApiRestarts++;
                AppendLine(chaosPath, $"{DateTimeOffset.UtcNow:O} api_restart");
            }

            // INFRA CHAOS: restart Postgres occasionally during replay windows.
            if (pg != null && sentSales % 350 == 0)
            {
                try
                {
                    AppendLine(chaosPath, $"{DateTimeOffset.UtcNow:O} postgres_restart_begin kind={pg.Kind}");
                    await pg.RestartAsync(cts.Token);
                    actions.PostgresRestarts++;
                    AppendLine(chaosPath, $"{DateTimeOffset.UtcNow:O} postgres_restart_end");
                }
                catch (Exception ex)
                {
                    actions.PostgresRestartFailures++;
                    AppendLine(chaosPath, $"{DateTimeOffset.UtcNow:O} postgres_restart_failed err=\"{ex.Message}\"");
                }
            }

            // TERMINAL CHAOS: random pause/resume replay via ops endpoints
            if (sentSales % 125 == 0)
            {
                var ti = terminalsInfo[rnd.Next(terminalsInfo.Count)];
                var pause = rnd.NextDouble() < 0.5;
                var endpoint = pause ? "/ops/terminal/pause-replay" : "/ops/terminal/resume-replay";
                await PostOpsAsync(http, endpoint, new
                {
                    tenantId = TenantId,
                    branchId = BranchId,
                    terminalId = ti.TerminalId,
                    actorId = (Guid?)null,
                    requestId = (string?)null,
                    reason = pause ? "burnin_pause" : "burnin_resume",
                    confirm = true,
                    metaJson = "{\"source\":\"burnin\"}"
                }, cts.Token);
                if (pause) actions.PauseReplay++; else actions.ResumeReplay++;
                AppendLine(chaosPath, $"{DateTimeOffset.UtcNow:O} {(pause ? "pause_replay" : "resume_replay")} terminal={ti.TerminalId:D}");
            }

            // SAMPLE: query fleet stats periodically and write a row (for summary later)
            if (sentSales % 100 == 0)
            {
                try
                {
                    var health = await GetOpsJsonAsync(http, $"/ops/replay/health?tenantId={TenantId:D}&branchId={BranchId:D}", cts.Token);
                    AppendLine(timelinePath, $"{DateTimeOffset.UtcNow:O} replay_health {health.ToString().Replace("\n", "")}");
                    actions.HealthSamples++;
                }
                catch (Exception ex)
                {
                    actions.OpsSampleFailures++;
                    AppendLine(timelinePath, $"{DateTimeOffset.UtcNow:O} replay_health_failed err=\"{ex.Message}\"");
                }
            }

            await Task.Delay(150, cts.Token);
        }

        // Best-effort drain: give replay time to converge after last chaos event.
        // IMPORTANT: scope to *this run's terminals* to avoid historical/stale mirror contamination.
        var drainOk = await WaitForReplayHealthyAsync(http, TenantId, BranchId, terminalsInfo.Select(x => x.TerminalId).ToArray(), timeout: TimeSpan.FromMinutes(2), cts.Token);

        // Final summary snapshot (best-effort)
        var summary = new BurnInSummary
        {
            Preset = preset,
            DurationMinutes = (int)duration.TotalMinutes,
            Terminals = terminals,
            StartedAt = startAt,
            EndedAt = DateTimeOffset.UtcNow,
            Counters = actions,
            DrainOk = drainOk
        };

        await File.WriteAllTextAsync(Path.Combine(outDir, "burnin-results.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), cts.Token);
        await File.WriteAllTextAsync(Path.Combine(outDir, "burnin-summary.csv"),
            BurnInSummaryToCsv(summary), cts.Token);

        var fin = await BuildFinancialIntegrityReportAsync(summary, startAt, DateTimeOffset.UtcNow, terminalsInfo, cts.Token);
        await File.WriteAllTextAsync(Path.Combine(outDir, "financial-integrity-report.json"),
            JsonSerializer.Serialize(fin, new JsonSerializerOptions { WriteIndented = true }), cts.Token);

        var checklist = BuildChecklistMd(summary, fin);
        await File.WriteAllTextAsync(Path.Combine(outDir, "production-readiness-checklist.md"), checklist, cts.Token);

        var signoff = BuildFinalSignoffMd(summary, fin);
        await File.WriteAllTextAsync(Path.Combine(outDir, "final-signoff.md"), signoff, cts.Token);

        var pass = fin.Pass;
        Console.WriteLine($"Burn-in completed. preset={preset} terminals={terminals} duration={duration} sales_enqueued={sentSales} out={outDir} pass={(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 2;
    }

    private static async Task RunTriageAsync(string runDir, CancellationToken ct)
    {
        var resultsPath = Path.Combine(runDir, "burnin-results.json");
        if (!File.Exists(resultsPath))
            throw new FileNotFoundException("burnin-results.json not found", resultsPath);

        var doc = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(resultsPath, ct));
        var startedAt = DateTimeOffset.Parse(doc.GetProperty("StartedAt").GetString()!);
        var endedAt = DateTimeOffset.Parse(doc.GetProperty("EndedAt").GetString()!);
        var startedAtMs = startedAt.ToUnixTimeMilliseconds();
        var endedAtMs = endedAt.ToUnixTimeMilliseconds();

        Console.WriteLine($"[triage] runDir={runDir}");
        Console.WriteLine($"[triage] startedAt={startedAt:O} endedAt={endedAt:O}");

        await using var conn = new Npgsql.NpgsqlConnection(Cs);
        await conn.OpenAsync(ct);

        // Identify burn-in terminals created near this run (name prefix + time window).
        var burnTerminalIds = new List<Guid>();
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                              SELECT terminal_id
                                FROM terminals
                               WHERE tenant_id=@t AND branch_id=@b
                                 AND name LIKE 'BurnIn-%'
                                 AND created_at >= @from AND created_at <= @to;
                              """;
            cmd.Parameters.AddWithValue("@t", TenantId);
            cmd.Parameters.AddWithValue("@b", BranchId);
            cmd.Parameters.AddWithValue("@from", startedAt.AddMinutes(-10));
            cmd.Parameters.AddWithValue("@to", endedAt.AddMinutes(10));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                burnTerminalIds.Add(r.GetGuid(0));
        }
        Console.WriteLine($"[triage] burn_terminals_detected={burnTerminalIds.Count}");

        // Helper: print top contributors.
        async Task PrintTopAsync(string title, string sql)
        {
            Console.WriteLine($"[triage] {title}");
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@t", TenantId);
            cmd.Parameters.AddWithValue("@b", BranchId);
            cmd.Parameters.AddWithValue("@s", startedAtMs);
            cmd.Parameters.AddWithValue("@e", endedAtMs);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var i = 0;
            while (await r.ReadAsync(ct))
            {
                i++;
                var term = r.GetGuid(0);
                var cnt = r.GetInt64(1);
                var minMs = r.IsDBNull(2) ? (long?)null : r.GetInt64(2);
                var maxMs = r.IsDBNull(3) ? (long?)null : r.GetInt64(3);
                Console.WriteLine($"  - terminal={term:D} count={cnt} minMs={minMs} maxMs={maxMs}");
                if (i >= 15) break;
            }
            if (i == 0) Console.WriteLine("  (none)");
        }

        await PrintTopAsync("dead_letters_by_terminal (all time)", """
            SELECT terminal_id, COUNT(*) AS cnt, MIN(failed_at_ms) AS min_ms, MAX(failed_at_ms) AS max_ms
              FROM terminal_dead_letters
             WHERE tenant_id=@t AND branch_id=@b
             GROUP BY terminal_id
             ORDER BY cnt DESC;
            """);

        await PrintTopAsync("dead_letters_by_terminal (this run window)", """
            SELECT terminal_id, COUNT(*) AS cnt, MIN(failed_at_ms) AS min_ms, MAX(failed_at_ms) AS max_ms
              FROM terminal_dead_letters
             WHERE tenant_id=@t AND branch_id=@b
               AND failed_at_ms >= @s AND failed_at_ms <= @e
             GROUP BY terminal_id
             ORDER BY cnt DESC;
            """);

        await PrintTopAsync("stuck_inflight_by_terminal (state=inflight, cutoff=end-60s)", """
            SELECT terminal_id, COUNT(*) AS cnt, MIN(inflight_at_ms) AS min_ms, MAX(inflight_at_ms) AS max_ms
              FROM terminal_replay_items
             WHERE tenant_id=@t AND branch_id=@b
               AND state='inflight'
               AND inflight_at_ms IS NOT NULL
               AND inflight_at_ms <= (@e - 60000)
             GROUP BY terminal_id
             ORDER BY cnt DESC;
            """);

        // Show oldest mirrored backlog age to detect stale runs.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                              SELECT MIN(created_at_ms)
                                FROM terminal_replay_items
                               WHERE tenant_id=@t AND branch_id=@b AND state='pending';
                              """;
            cmd.Parameters.AddWithValue("@t", TenantId);
            cmd.Parameters.AddWithValue("@b", BranchId);
            var o = await cmd.ExecuteScalarAsync(ct);
            var oldest = o == null || o is DBNull ? (long?)null : Convert.ToInt64(o);
            Console.WriteLine($"[triage] oldest_pending_created_at_ms={oldest} (age_ms={(oldest.HasValue ? (endedAtMs - oldest.Value) : 0)})");
        }

        // Residual divergence terminals.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                              SELECT terminal_id, COALESCE(health_state,'offline') AS hs, last_seen_at
                                FROM terminal_status
                               WHERE tenant_id=@t AND branch_id=@b
                                 AND COALESCE(health_state,'offline') IN ('divergent','recovering')
                               ORDER BY last_seen_at NULLS FIRST;
                              """;
            cmd.Parameters.AddWithValue("@t", TenantId);
            cmd.Parameters.AddWithValue("@b", BranchId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var i = 0;
            Console.WriteLine("[triage] unrecovered_divergence_terminals");
            while (await r.ReadAsync(ct))
            {
                i++;
                var term = r.GetGuid(0);
                var hs = r.GetString(1);
                var lastSeen = r.IsDBNull(2) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(2);
                Console.WriteLine($"  - terminal={term:D} hs={hs} last_seen_at={lastSeen:O}");
                if (i >= 30) break;
            }
            if (i == 0) Console.WriteLine("  (none)");
        }

        if (burnTerminalIds.Count > 0)
        {
            // Scoped counts for just this run's terminals.
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                              SELECT
                                (SELECT COUNT(*) FROM terminal_dead_letters WHERE tenant_id=@t AND branch_id=@b AND terminal_id = ANY(@terms)) AS dead_all,
                                (SELECT COUNT(*) FROM terminal_dead_letters WHERE tenant_id=@t AND branch_id=@b AND terminal_id = ANY(@terms) AND failed_at_ms >= @s AND failed_at_ms <= @e) AS dead_window,
                                (SELECT COUNT(*) FROM terminal_replay_items WHERE tenant_id=@t AND branch_id=@b AND terminal_id = ANY(@terms) AND state='inflight' AND inflight_at_ms IS NOT NULL AND inflight_at_ms <= (@e - 60000)) AS stuck_inflight,
                                (SELECT COUNT(*) FROM terminal_status WHERE tenant_id=@t AND branch_id=@b AND terminal_id = ANY(@terms) AND COALESCE(health_state,'offline') IN ('divergent','recovering')) AS unrecovered_div;
                              """;
            cmd.Parameters.AddWithValue("@t", TenantId);
            cmd.Parameters.AddWithValue("@b", BranchId);
            cmd.Parameters.AddWithValue("@terms", burnTerminalIds.ToArray());
            cmd.Parameters.AddWithValue("@s", startedAtMs);
            cmd.Parameters.AddWithValue("@e", endedAtMs);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                Console.WriteLine($"[triage] scoped_to_run_terminals dead_all={r.GetInt64(0)} dead_window={r.GetInt64(1)} stuck_inflight={r.GetInt64(2)} unrecovered_div={r.GetInt64(3)}");
            }
        }
    }

    private const string Cs = "Host=127.0.0.1;Port=5432;Database=posedgedb;Username=posedge;Password=posedge;Include Error Detail=true";
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BranchId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static async Task ProvisionTerminalAndCashAsync(Guid terminalId, Guid cashId, CancellationToken ct)
    {
        await using var conn = new Npgsql.NpgsqlConnection(Cs);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          INSERT INTO terminals(terminal_id, tenant_id, branch_id, name, machine_name, status)
                          VALUES (@tid, '11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222',
                                  @name, @machine, 'active')
                          ON CONFLICT (terminal_id) DO NOTHING;

                          INSERT INTO cash_sessions(cash_session_id, tenant_id, branch_id, terminal_id, opened_by, opening_amount, status)
                          VALUES (@cid, '11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222',
                                  @tid, '99999999-9999-9999-9999-999999999999', 0, 'open')
                          ON CONFLICT (cash_session_id) DO NOTHING;
                          """;
        cmd.Parameters.AddWithValue("@tid", terminalId);
        cmd.Parameters.AddWithValue("@cid", cashId);
        cmd.Parameters.AddWithValue("@name", "BurnIn-" + terminalId.ToString("N")[..8]);
        cmd.Parameters.AddWithValue("@machine", "BURN-" + terminalId.ToString("N")[..8]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ResetInventoryAsync(decimal onHand, decimal reserved, CancellationToken ct)
    {
        await using var conn = new Npgsql.NpgsqlConnection(Cs);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          UPDATE inventory
                             SET on_hand = @on,
                                 reserved = @res,
                                 updated_at = now()
                           WHERE tenant_id = '11111111-1111-1111-1111-111111111111'
                             AND branch_id = '22222222-2222-2222-2222-222222222222'
                             AND product_id = '33333333-3333-3333-3333-333333333333';
                          """;
        cmd.Parameters.AddWithValue("@on", onHand);
        cmd.Parameters.AddWithValue("@res", reserved);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AppendLine(string path, string line)
    {
        try { File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8); } catch { }
    }

    private static async Task<JsonElement> GetOpsJsonAsync(HttpClient http, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("x-ops-key", "dev-ops-key");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return doc;
    }

    private static async Task PostOpsAsync(HttpClient http, string path, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path);
        req.Headers.Add("x-ops-key", "dev-ops-key");
        req.Content = JsonContent.Create(body);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    private static string BurnInSummaryToCsv(BurnInSummary s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("preset,duration_minutes,terminals,sales_enqueued,api_restarts,postgres_restarts,pause_replay,resume_replay,health_samples,drain_ok");
        sb.Append(s.Preset).Append(',')
          .Append(s.DurationMinutes).Append(',')
          .Append(s.Terminals).Append(',')
          .Append(s.Counters.SalesEnqueued).Append(',')
          .Append(s.Counters.ApiRestarts).Append(',')
          .Append(s.Counters.PostgresRestarts).Append(',')
          .Append(s.Counters.PauseReplay).Append(',')
          .Append(s.Counters.ResumeReplay).Append(',')
          .Append(s.Counters.HealthSamples).Append(',')
          .Append(s.DrainOk ? "1" : "0").AppendLine();
        return sb.ToString();
    }

    private static string BuildChecklistMd(BurnInSummary s, FinancialIntegrityReport fin)
    {
        string Pf(bool b) => b ? "PASS" : "FAIL";
        return $"""
               ## Production readiness (burn-in)

               - **Preset**: {s.Preset}
               - **Duration**: {s.DurationMinutes} min
               - **Terminals**: {s.Terminals}
               - **Sales enqueued**: {s.Counters.SalesEnqueued}
               - **API restarts**: {s.Counters.ApiRestarts}
               - **Postgres restarts**: {s.Counters.PostgresRestarts} (failures={s.Counters.PostgresRestartFailures})
               - **Replay pause/resume**: {s.Counters.PauseReplay}/{s.Counters.ResumeReplay}
               - **Replay drained**: {Pf(s.DrainOk)}

               ## PASS/FAIL final
               - **OVERALL**: {Pf(fin.Pass)}

               ### Financial integrity
               - **duplicate_sales == 0**: {Pf(fin.Metrics.DuplicateSales == 0)}
               - **duplicate_refunds == 0**: {Pf(fin.Metrics.DuplicateRefunds == 0)}
               - **journal_imbalances == 0**: {Pf(fin.Metrics.JournalImbalances == 0)}
               - **orphan_records == 0**: {Pf(fin.Metrics.OrphanRecords == 0)}
               - **unresolved_discrepancies == 0**: {Pf(fin.Metrics.UnresolvedDiscrepancies == 0)}

               ### Multicaja resilience
               - **dead_letter_count <= {fin.Thresholds.DeadLetterCount}**: {Pf(fin.Metrics.DeadLetterCount <= fin.Thresholds.DeadLetterCount)}
               - **stuck_inflight == 0**: {Pf(fin.Metrics.StuckInflight == 0)}
               - **unrecovered_divergence == 0**: {Pf(fin.Metrics.UnrecoveredDivergence == 0)}
               - **replay_success_rate >= {fin.Thresholds.ReplaySuccessRateMin:0.000}**: {Pf(fin.Metrics.ReplaySuccessRate >= fin.Thresholds.ReplaySuccessRateMin)}

               ### Chaos resilience
               - **postgres_restart_failures == 0**: {Pf(s.Counters.PostgresRestartFailures == 0)}
               - **process_crashes == 0**: {Pf(fin.Metrics.ProcessCrashes == 0)}

               ## Residual risks / limitations (explicit)
               - Burn-in traffic currently stresses **Sale.Commit + replay + infra chaos** primarily; financial flows not actively generated unless they occur naturally during the run.
               - `event_log.seq` can have identity holes; terminals now tolerate this by checkpoint bumping.
               - Lease renew may fail transiently under heavy chaos; sales may be throttled by LEASES_ONLY safety.
               """;
    }

    private static string BuildFinalSignoffMd(BurnInSummary s, FinancialIntegrityReport fin)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Final signoff (multicaja)");
        sb.AppendLine();
        sb.AppendLine($"**Result: {(fin.Pass ? "PASS" : "FAIL")}**");
        sb.AppendLine();
        sb.AppendLine("### Run");
        sb.AppendLine($"- preset: `{s.Preset}`");
        sb.AppendLine($"- duration_minutes: `{s.DurationMinutes}`");
        sb.AppendLine($"- terminals: `{s.Terminals}`");
        sb.AppendLine($"- started_at: `{s.StartedAt:O}`");
        sb.AppendLine($"- ended_at: `{s.EndedAt:O}`");
        sb.AppendLine();
        sb.AppendLine("### Replay stability");
        sb.AppendLine($"- drain_ok: `{s.DrainOk}`");
        sb.AppendLine($"- replay_success_rate: `{fin.Metrics.ReplaySuccessRate:0.0000}` (min `{fin.Thresholds.ReplaySuccessRateMin:0.0000}`)");
        sb.AppendLine($"- dead_letter_count: `{fin.Metrics.DeadLetterCount}` (max `{fin.Thresholds.DeadLetterCount}`)");
        sb.AppendLine($"- stuck_inflight: `{fin.Metrics.StuckInflight}` (max `{fin.Thresholds.StuckInflight}`)");
        sb.AppendLine($"- unrecovered_divergence: `{fin.Metrics.UnrecoveredDivergence}` (max `{fin.Thresholds.UnrecoveredDivergence}`)");
        sb.AppendLine();
        sb.AppendLine("### Chaos resilience");
        sb.AppendLine($"- api_restarts: `{s.Counters.ApiRestarts}`");
        sb.AppendLine($"- postgres_restarts: `{s.Counters.PostgresRestarts}` (failures `{s.Counters.PostgresRestartFailures}`)");
        sb.AppendLine($"- replay_pause/resume: `{s.Counters.PauseReplay}`/`{s.Counters.ResumeReplay}`");
        sb.AppendLine();
        if (fin.Failures.Count > 0)
        {
            sb.AppendLine("### Failures");
            foreach (var f in fin.Failures)
                sb.AppendLine($"- `{f}`");
            sb.AppendLine();
        }
        sb.AppendLine("### Production readiness");
        sb.AppendLine(fin.Pass
            ? "- Declared **production-ready operational** for replay/recovery under configured chaos targets."
            : "- **NOT** production-ready operational; see failures above and checklist.");
        sb.AppendLine();
        return sb.ToString();
    }

    private sealed record TerminalInfo(Guid TerminalId, Guid CashSessionId, string ReplicaPath, Process Process);

    public sealed class BurnInCounters
    {
        public long SalesEnqueued { get; set; }
        public long ApiRestarts { get; set; }
        public long PostgresRestarts { get; set; }
        public long PostgresRestartFailures { get; set; }
        public long PauseReplay { get; set; }
        public long ResumeReplay { get; set; }
        public long HealthSamples { get; set; }
        public long OpsSampleFailures { get; set; }
    }

    public sealed class BurnInSummary
    {
        public string Preset { get; set; } = "SMOKE";
        public int DurationMinutes { get; set; }
        public int Terminals { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset EndedAt { get; set; }
        public BurnInCounters Counters { get; set; } = new();
        public bool DrainOk { get; set; }
    }

    public sealed class FinancialIntegrityReport
    {
        public bool Pass { get; set; }
        public BurnInMetrics Metrics { get; set; } = new();
        public BurnInThresholds Thresholds { get; set; } = new();
        public List<string> Failures { get; set; } = new();
    }

    public sealed class BurnInMetrics
    {
        public long DuplicateSales { get; set; }
        public long DuplicateRefunds { get; set; }
        public long JournalImbalances { get; set; }
        public long UnresolvedDiscrepancies { get; set; }
        public long OrphanRecords { get; set; }
        public int DeadLetterCount { get; set; }
        public int StuckInflight { get; set; }
        public int UnrecoveredDivergence { get; set; }
        public decimal ReplaySuccessRate { get; set; }
        public decimal ReplayRetryRate { get; set; }
        public decimal DeadLetterRate { get; set; }
        public long SnapshotRecoveryCount { get; set; }
        public long DivergenceCount { get; set; }
        public long ReplayDrainAvgMs { get; set; }
        public long AverageRecoveryMs { get; set; }
        public long ProcessCrashes { get; set; }
        public long MemoryGrowthMb { get; set; }
    }

    public sealed class BurnInThresholds
    {
        public long DuplicateSales { get; set; } = 0;
        public long DuplicateRefunds { get; set; } = 0;
        public long JournalImbalances { get; set; } = 0;
        public long UnresolvedDiscrepancies { get; set; } = 0;
        public long OrphanRecords { get; set; } = 0;
        public int DeadLetterCount { get; set; } = 0;
        public int StuckInflight { get; set; } = 0;
        public int UnrecoveredDivergence { get; set; } = 0;
        public decimal ReplaySuccessRateMin { get; set; } = 0.99m;
        public long ProcessCrashes { get; set; } = 0;
        public long MemoryGrowthMbMax { get; set; } = 512;
    }

    private static async Task<bool> WaitForReplayHealthyAsync(HttpClient http, Guid tenantId, Guid branchId, Guid[] terminalIds, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // Prefer DB-scoped drain check (authoritative for burn-in), since /ops/replay/health is branch-global.
        await using var conn = new Npgsql.NpgsqlConnection(Cs);
        await conn.OpenAsync(ct);
        while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                                  SELECT
                                    (SELECT COUNT(*)
                                       FROM terminal_replay_items
                                      WHERE tenant_id=@t AND branch_id=@b
                                        AND terminal_id = ANY(@terms)
                                        AND state IN ('pending','retry_wait','inflight')) AS backlog,
                                    (SELECT COUNT(*)
                                       FROM terminal_dead_letters
                                      WHERE tenant_id=@t AND branch_id=@b
                                        AND terminal_id = ANY(@terms)) AS dead,
                                    (SELECT COUNT(*)
                                       FROM terminal_replay_items
                                      WHERE tenant_id=@t AND branch_id=@b
                                        AND terminal_id = ANY(@terms)
                                        AND state='inflight'
                                        AND inflight_at_ms IS NOT NULL
                                        AND inflight_at_ms <= @cutoff) AS stuck;
                                  """;
                cmd.Parameters.AddWithValue("@t", tenantId);
                cmd.Parameters.AddWithValue("@b", branchId);
                cmd.Parameters.AddWithValue("@terms", terminalIds);
                cmd.Parameters.AddWithValue("@cutoff", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60000);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    var backlog = r.GetInt64(0);
                    var dead = r.GetInt64(1);
                    var stuck = r.GetInt64(2);
                    if (backlog == 0 && dead <= 1 && stuck == 0)
                        return true;
                }
            }
            catch { }
            try { await Task.Delay(500, ct); } catch { }
        }
        return false;
    }

    private static async Task<FinancialIntegrityReport> BuildFinancialIntegrityReportAsync(
        BurnInSummary summary,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        List<TerminalInfo> terminals,
        CancellationToken ct)
    {
        var rep = new FinancialIntegrityReport();
        rep.Thresholds = new BurnInThresholds
        {
            // Final acceptance thresholds (smoke + burn-in).
            DeadLetterCount = 1,
            ReplaySuccessRateMin = 0.995m,
            StuckInflight = 0,
            UnrecoveredDivergence = 0
        };
        rep.Metrics = new BurnInMetrics();

        var startedAtMs = startedAt.ToUnixTimeMilliseconds();
        var endedAtMs = endedAt.ToUnixTimeMilliseconds();
        var termIds = terminals.Select(t => t.TerminalId).ToArray();

        // Process crashes + memory growth (best-effort; terminal processes may have exited after runner finishes).
        long crashes = 0;
        long memStart = 0;
        long memEnd = 0;
        foreach (var t in terminals)
        {
            if (t.Process.HasExited) crashes++;
            memEnd += SafeWorkingSetMb(t.Process);
        }
        rep.Metrics.ProcessCrashes = crashes;
        rep.Metrics.MemoryGrowthMb = Math.Max(0, memEnd - memStart);

        await using var conn = new Npgsql.NpgsqlConnection(Cs);
        await conn.OpenAsync(ct);

        // Duplicate sales by request_id
        rep.Metrics.DuplicateSales = await ScalarLongAsync(conn, """
            SELECT COALESCE(SUM(x.cnt - 1), 0)
              FROM (
                    SELECT COUNT(*) AS cnt
                      FROM sales
                     WHERE tenant_id=@t AND branch_id=@b AND created_at >= @start AND created_at <= @end
                     GROUP BY request_id
                    HAVING COUNT(*) > 1
                   ) x;
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@t", TenantId);
            cmd.Parameters.AddWithValue("@b", BranchId);
            cmd.Parameters.AddWithValue("@start", startedAt);
            cmd.Parameters.AddWithValue("@end", endedAt);
        }, ct);

        // Duplicate refunds by request_id
        rep.Metrics.DuplicateRefunds = await ScalarLongAsync(conn, """
            SELECT COALESCE(SUM(x.cnt - 1), 0)
              FROM (
                    SELECT COUNT(*) AS cnt
                      FROM refunds
                     WHERE tenant_id=@t AND branch_id=@b AND created_at >= @start AND created_at <= @end
                     GROUP BY request_id
                    HAVING COUNT(*) > 1
                   ) x;
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@t", TenantId);
            cmd.Parameters.AddWithValue("@b", BranchId);
            cmd.Parameters.AddWithValue("@start", startedAt);
            cmd.Parameters.AddWithValue("@end", endedAt);
        }, ct);

        // Journal imbalance (append-only integrity invariant)
        rep.Metrics.JournalImbalances = await ScalarLongAsync(conn, """
            SELECT COUNT(*)
              FROM financial_journal
             WHERE tenant_id=@t AND branch_id=@b
               AND ABS(total_debit - total_credit) > 0.0001;
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@t", TenantId);
            cmd.Parameters.AddWithValue("@b", BranchId);
        }, ct);

        // Unresolved discrepancies
        rep.Metrics.UnresolvedDiscrepancies = await ScalarLongAsync(conn, """
            SELECT COUNT(*)
              FROM cash_discrepancies d
             WHERE d.tenant_id=@t AND d.branch_id=@b
               AND NOT EXISTS (
                 SELECT 1 FROM cash_discrepancy_resolutions r
                  WHERE r.tenant_id=d.tenant_id AND r.branch_id=d.branch_id AND r.discrepancy_id=d.discrepancy_id
               );
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@t", TenantId);
            cmd.Parameters.AddWithValue("@b", BranchId);
        }, ct);

        // Orphan records (FK-like invariants)
        var orphanPayments = await ScalarLongAsync(conn, """
            SELECT COUNT(*)
              FROM payments p
              LEFT JOIN sales s ON s.sale_id = p.sale_id
             WHERE s.sale_id IS NULL;
            """, _ => { }, ct);
        var orphanSaleLines = await ScalarLongAsync(conn, """
            SELECT COUNT(*)
              FROM sale_lines l
              LEFT JOIN sales s ON s.sale_id = l.sale_id
             WHERE s.sale_id IS NULL;
            """, _ => { }, ct);
        var orphanRefundLines = await ScalarLongAsync(conn, """
            SELECT COUNT(*)
              FROM refund_lines l
              LEFT JOIN refunds r ON r.refund_id = l.refund_id
             WHERE r.refund_id IS NULL;
            """, _ => { }, ct);
        var orphanRefundPayments = await ScalarLongAsync(conn, """
            SELECT COUNT(*)
              FROM refund_payments p
              LEFT JOIN refunds r ON r.refund_id = p.refund_id
             WHERE r.refund_id IS NULL;
            """, _ => { }, ct);
        rep.Metrics.OrphanRecords = orphanPayments + orphanSaleLines + orphanRefundLines + orphanRefundPayments;

        // Replay health snapshot
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://localhost") };
            _ = http;
        }

        // Dead-letter + inflight + throughput rates from mirrored logs
        rep.Metrics.DeadLetterCount = (int)await ScalarLongAsync(conn, """
            SELECT COUNT(*)
              FROM terminal_dead_letters
             WHERE tenant_id=@t AND branch_id=@b
               AND terminal_id = ANY(@terms);
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@t", TenantId);
            cmd.Parameters.AddWithValue("@b", BranchId);
            cmd.Parameters.AddWithValue("@terms", termIds);
        }, ct);

        rep.Metrics.StuckInflight = (int)await ScalarLongAsync(conn, """
            SELECT COUNT(*)
              FROM terminal_replay_items
             WHERE tenant_id=@t AND branch_id=@b
               AND terminal_id = ANY(@terms)
               AND state='inflight'
               AND inflight_at_ms IS NOT NULL
               AND inflight_at_ms <= @cutoff;
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@t", TenantId);
            cmd.Parameters.AddWithValue("@b", BranchId);
            cmd.Parameters.AddWithValue("@terms", termIds);
            cmd.Parameters.AddWithValue("@cutoff", endedAtMs - 60000);
        }, ct);

        rep.Metrics.UnrecoveredDivergence = (int)await ScalarLongAsync(conn, """
            SELECT COUNT(*)
              FROM terminal_status
             WHERE tenant_id=@t AND branch_id=@b
               AND terminal_id = ANY(@terms)
               AND COALESCE(health_state,'offline') IN ('divergent','recovering');
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@t", TenantId);
            cmd.Parameters.AddWithValue("@b", BranchId);
            cmd.Parameters.AddWithValue("@terms", termIds);
        }, ct);

        rep.Metrics.SnapshotRecoveryCount = await ScalarLongAsync(conn, """
            SELECT COUNT(*)
              FROM terminal_replay_log
             WHERE tenant_id=@t AND branch_id=@b
               AND terminal_id = ANY(@terms)
               AND kind='snapshot_completed'
               AND at_ms >= @s AND at_ms <= @e;
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@t", TenantId);
            cmd.Parameters.AddWithValue("@b", BranchId);
            cmd.Parameters.AddWithValue("@terms", termIds);
            cmd.Parameters.AddWithValue("@s", startedAtMs);
            cmd.Parameters.AddWithValue("@e", endedAtMs);
        }, ct);

        rep.Metrics.DivergenceCount = await ScalarLongAsync(conn, """
            SELECT COUNT(*)
              FROM terminal_replay_log
             WHERE tenant_id=@t AND branch_id=@b
               AND terminal_id = ANY(@terms)
               AND kind='divergence_detected'
               AND at_ms >= @s AND at_ms <= @e;
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@t", TenantId);
            cmd.Parameters.AddWithValue("@b", BranchId);
            cmd.Parameters.AddWithValue("@terms", termIds);
            cmd.Parameters.AddWithValue("@s", startedAtMs);
            cmd.Parameters.AddWithValue("@e", endedAtMs);
        }, ct);

        var acks = await ScalarLongAsync(conn, """
            SELECT COUNT(*)
              FROM terminal_replay_log
             WHERE tenant_id=@t AND branch_id=@b
               AND terminal_id = ANY(@terms)
               AND kind='ack'
               AND at_ms >= @s AND at_ms <= @e;
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@t", TenantId);
            cmd.Parameters.AddWithValue("@b", BranchId);
            cmd.Parameters.AddWithValue("@terms", termIds);
            cmd.Parameters.AddWithValue("@s", startedAtMs);
            cmd.Parameters.AddWithValue("@e", endedAtMs);
        }, ct);
        var retries = await ScalarLongAsync(conn, """
            SELECT COUNT(*)
              FROM terminal_replay_log
             WHERE tenant_id=@t AND branch_id=@b
               AND terminal_id = ANY(@terms)
               AND kind='retry'
               AND at_ms >= @s AND at_ms <= @e;
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@t", TenantId);
            cmd.Parameters.AddWithValue("@b", BranchId);
            cmd.Parameters.AddWithValue("@terms", termIds);
            cmd.Parameters.AddWithValue("@s", startedAtMs);
            cmd.Parameters.AddWithValue("@e", endedAtMs);
        }, ct);
        var dead = await ScalarLongAsync(conn, """
            SELECT COUNT(*)
              FROM terminal_replay_log
             WHERE tenant_id=@t AND branch_id=@b
               AND terminal_id = ANY(@terms)
               AND kind='dead'
               AND at_ms >= @s AND at_ms <= @e;
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@t", TenantId);
            cmd.Parameters.AddWithValue("@b", BranchId);
            cmd.Parameters.AddWithValue("@terms", termIds);
            cmd.Parameters.AddWithValue("@s", startedAtMs);
            cmd.Parameters.AddWithValue("@e", endedAtMs);
        }, ct);

        var denom1 = acks + dead;
        rep.Metrics.ReplaySuccessRate = denom1 == 0 ? 1m : (decimal)acks / (decimal)denom1;
        var denom2 = acks + dead + retries;
        rep.Metrics.ReplayRetryRate = denom2 == 0 ? 0m : (decimal)retries / (decimal)denom2;
        rep.Metrics.DeadLetterRate = denom1 == 0 ? 0m : (decimal)dead / (decimal)denom1;

        // Replay drain average: enqueue->ack per requestId (best-effort)
        rep.Metrics.ReplayDrainAvgMs = await ScalarLongAsync(conn, """
            WITH x AS (
              SELECT request_id,
                     MIN(at_ms) FILTER (WHERE kind='enqueue') AS enq,
                     MIN(at_ms) FILTER (WHERE kind='ack') AS ack
                FROM terminal_replay_log
               WHERE tenant_id=@t AND branch_id=@b
                 AND terminal_id = ANY(@terms)
                 AND request_id IS NOT NULL
                 AND at_ms >= @s AND at_ms <= @e
               GROUP BY request_id
            )
            SELECT COALESCE(AVG(ack - enq), 0)::bigint
              FROM x
             WHERE enq IS NOT NULL AND ack IS NOT NULL AND ack >= enq;
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@t", TenantId);
            cmd.Parameters.AddWithValue("@b", BranchId);
            cmd.Parameters.AddWithValue("@terms", termIds);
            cmd.Parameters.AddWithValue("@s", startedAtMs);
            cmd.Parameters.AddWithValue("@e", endedAtMs);
        }, ct);

        // Average recovery time: divergence_detected -> snapshot_completed (best-effort, per terminal)
        rep.Metrics.AverageRecoveryMs = await ScalarLongAsync(conn, """
            WITH d AS (
              SELECT terminal_id, MIN(at_ms) AS at_ms
                FROM terminal_replay_log
               WHERE tenant_id=@t AND branch_id=@b
                 AND terminal_id = ANY(@terms)
                 AND kind='divergence_detected'
                 AND at_ms >= @s AND at_ms <= @e
               GROUP BY terminal_id
            ),
            s AS (
              SELECT terminal_id, MIN(at_ms) AS at_ms
                FROM terminal_replay_log
               WHERE tenant_id=@t AND branch_id=@b
                 AND terminal_id = ANY(@terms)
                 AND kind='snapshot_completed'
                 AND at_ms >= @s AND at_ms <= @e
               GROUP BY terminal_id
            )
            SELECT COALESCE(AVG(s.at_ms - d.at_ms), 0)::bigint
              FROM d JOIN s USING (terminal_id)
             WHERE s.at_ms >= d.at_ms;
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@t", TenantId);
            cmd.Parameters.AddWithValue("@b", BranchId);
            cmd.Parameters.AddWithValue("@terms", termIds);
            cmd.Parameters.AddWithValue("@s", startedAtMs);
            cmd.Parameters.AddWithValue("@e", endedAtMs);
        }, ct);

        // Pass/fail evaluation
        var fails = new List<string>();
        void Check(string name, bool ok) { if (!ok) fails.Add(name); }

        Check("drain_ok", summary.DrainOk);
        Check("duplicate_sales", rep.Metrics.DuplicateSales <= rep.Thresholds.DuplicateSales);
        Check("duplicate_refunds", rep.Metrics.DuplicateRefunds <= rep.Thresholds.DuplicateRefunds);
        Check("journal_imbalances", rep.Metrics.JournalImbalances <= rep.Thresholds.JournalImbalances);
        Check("unresolved_discrepancies", rep.Metrics.UnresolvedDiscrepancies <= rep.Thresholds.UnresolvedDiscrepancies);
        Check("orphan_records", rep.Metrics.OrphanRecords <= rep.Thresholds.OrphanRecords);
        Check("dead_letter_count", rep.Metrics.DeadLetterCount <= rep.Thresholds.DeadLetterCount);
        Check("stuck_inflight", rep.Metrics.StuckInflight <= rep.Thresholds.StuckInflight);
        Check("unrecovered_divergence", rep.Metrics.UnrecoveredDivergence <= rep.Thresholds.UnrecoveredDivergence);
        Check("replay_success_rate", rep.Metrics.ReplaySuccessRate >= rep.Thresholds.ReplaySuccessRateMin);
        Check("process_crashes", rep.Metrics.ProcessCrashes <= rep.Thresholds.ProcessCrashes);
        Check("memory_growth_mb", rep.Metrics.MemoryGrowthMb <= rep.Thresholds.MemoryGrowthMbMax);

        rep.Failures = fails;
        rep.Pass = fails.Count == 0;
        return rep;
    }

    private static long SafeWorkingSetMb(Process p)
    {
        try { return p.WorkingSet64 / (1024 * 1024); } catch { return 0; }
    }

    private static async Task<long> ScalarLongAsync(Npgsql.NpgsqlConnection conn, string sql, Action<Npgsql.NpgsqlCommand> bind, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        var o = await cmd.ExecuteScalarAsync(ct);
        return o == null || o is DBNull ? 0L : Convert.ToInt64(o);
    }

    private static async Task WaitForActiveLeaseAsync(string sqlitePath, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            try
            {
                var cs = new SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
                using var conn = new SqliteConnection(cs);
                await conn.OpenAsync(ct);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM leases WHERE status='active' AND expires_at_ms > $now;";
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var c = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
                if (c > 0) return;
            }
            catch { }
            try { await Task.Delay(250, ct); } catch { }
        }
        // best-effort: proceed anyway
    }
}

