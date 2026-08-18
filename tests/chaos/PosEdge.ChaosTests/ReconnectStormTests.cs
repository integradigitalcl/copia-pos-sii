using Xunit;
using PosEdge.IntegrationTests;
using Npgsql;

namespace PosEdge.ChaosTests;

public sealed class ReconnectStormTests
{
    private const string Cs = "Host=127.0.0.1;Port=5432;Database=posedgedb;Username=posedge;Password=posedge;Include Error Detail=true";

    [Fact]
    public async Task Reconnect_storm_with_multiple_terminals_eventually_stabilizes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));

        var pg = await PostgresControllerFactory.DetectAsync(cts.Token);
        if (pg == null)
            throw new InvalidOperationException("No Postgres controller detected (Windows service / pg_ctl / docker).");

        await using var h = new ProcessHarness();
        var port = ProcessHarness.GetFreeTcpPort();
        var apiUrl = $"http://localhost:{port}";

        var repoRoot = ProcessHarness.FindRepoRoot(AppContext.BaseDirectory);
        var apiProj = Path.Combine(repoRoot, "src", "PosEdge.Api");
        var terminalProj = Path.Combine(repoRoot, "src", "PosEdge.Terminal");

        var apiEnv = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development"
        };

        // Start API
        var api = await h.StartDotnetAsync(apiProj, $"run -c Release --no-build --urls {apiUrl}", apiEnv);
        _ = api;
        await h.WaitForHttpOkAsync(apiUrl, "/health/ready", TimeSpan.FromSeconds(30));

        // Ensure inventory has headroom
        await ResetInventoryAsync(200m, 0m, cts.Token);

        // Pre-generate IDs so we can provision DB before terminals request leases.
        var termEnvBase = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["POSEDGE_LEASE_TARGET_UNITS_PRODUCT1"] = "5",
            ["POSEDGE_LEASE_TTL_SECONDS"] = "180",
            // Storm hardening config
            ["POSEDGE_STARTUP_RECONNECT_JITTER_MS"] = "3000",
            ["POSEDGE_REPLAY_START_JITTER_MS"] = "2000",
            ["POSEDGE_REPLAY_BATCH_SIZE"] = "10",
            ["POSEDGE_JITTER_SEED"] = "12345"
        };

        var terminalIds = new List<Guid>();
        var cashIds = new List<Guid>();
        var replicas = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            var replica = Path.Combine(Path.GetTempPath(), $"posedg-storm-{i}-{Guid.NewGuid():N}.sqlite");
            var termId = Guid.NewGuid();
            var cashId = Guid.NewGuid();
            terminalIds.Add(termId);
            cashIds.Add(cashId);
            replicas.Add(replica);
        }

        // Provision terminals + cash sessions in Postgres so Sale.Commit validations pass.
        await ProvisionTerminalsAndCashSessionsAsync(terminalIds, cashIds, cts.Token);

        // Spawn processes now.
        var terminals = new List<(System.Diagnostics.Process Proc, string ReplicaPath)>();
        for (var i = 0; i < 12; i++)
        {
            var env = new Dictionary<string, string>(termEnvBase);
            env["POSEDGE_TEST_DIVERGENT_HOLD_MS"] = "0";
            var p = await h.StartDotnetAsync(terminalProj, $"run -c Release --no-build -- {apiUrl} x \"{replicas[i]}\" {terminalIds[i]:D} {cashIds[i]:D}", env);
            terminals.Add((p, replicas[i]));
        }

        // Let them connect and acquire leases.
        await Task.WhenAll(terminals.Select(t => WaitForActiveLeaseAsync(t.ReplicaPath, TimeSpan.FromSeconds(60), cts.Token)));

        // Simulate infra outage: kill API (all terminals go offline)
        ProcessHarness.Kill(api);

        // Build offline backlog: each terminal enqueues 3 sales.
        foreach (var (p, _) in terminals)
        {
            for (var j = 0; j < 3; j++)
            {
                await p.StandardInput.WriteLineAsync("sale");
                await p.StandardInput.FlushAsync();
            }
        }

        // Ensure backlog exists in at least one replica
        await Task.Delay(2000, cts.Token);
        Assert.Contains(terminals, t => ReadOutboxCount(t.ReplicaPath) > 0);

        // Bring API back
        api = await h.StartDotnetAsync(apiProj, $"run -c Release --no-build --urls {apiUrl}", apiEnv);
        _ = api;
        await h.WaitForHttpOkAsync(apiUrl, "/health/ready", TimeSpan.FromSeconds(30));

        // Restart Postgres during the reconnect/replay storm
        await Task.Delay(1000, cts.Token);
        await pg.RestartAsync(cts.Token);

        // Eventually all outboxes should drain and no dead-letter should appear.
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cts.Token.ThrowIfCancellationRequested();
            var allDrained = terminals.All(t => ReadOutboxCount(t.ReplicaPath) == 0);
            if (allDrained) break;
            await Task.Delay(500, cts.Token);
        }

        Assert.All(terminals, t =>
        {
            Assert.Equal(0, ReadOutboxCount(t.ReplicaPath));
            Assert.Equal(0, ReadDeadLetterCount(t.ReplicaPath));
            Assert.Equal(0, ReadInflightCount(t.ReplicaPath));
            var st = ReadHealthState(t.ReplicaPath);
            Assert.DoesNotContain(st, new[] { "divergent", "revoked" });
        });

        // Exactly-once: each committed requestId exists once in server.
        var allReqIds = terminals.SelectMany(t => ReadCommittedRequestIds(t.ReplicaPath)).ToList();
        Assert.NotEmpty(allReqIds);
        Assert.Equal(allReqIds.Count, allReqIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var serverCount = await CountSalesByRequestIdsAsync(allReqIds, cts.Token);
        Assert.Equal(allReqIds.Count, serverCount);

        // Safety: server-side no oversell (inventory on_hand never negative). Here we just assert it's >= 0.
        var onHand = await ReadOnHandAsync(cts.Token);
        Assert.True(onHand >= 0m);
    }

    [Fact]
    public async Task Reconnect_storm_25_terminals_api_restart_mid_storm_eventually_drains()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));

        var pg = await PostgresControllerFactory.DetectAsync(cts.Token);
        if (pg == null)
            throw new InvalidOperationException("No Postgres controller detected (Windows service / pg_ctl / docker).");

        await using var h = new ProcessHarness();
        var port = ProcessHarness.GetFreeTcpPort();
        var apiUrl = $"http://localhost:{port}";

        var repoRoot = ProcessHarness.FindRepoRoot(AppContext.BaseDirectory);
        var apiProj = Path.Combine(repoRoot, "src", "PosEdge.Api");
        var terminalProj = Path.Combine(repoRoot, "src", "PosEdge.Terminal");

        var apiEnv = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development"
        };

        var api = await h.StartDotnetAsync(apiProj, $"run -c Release --no-build --urls {apiUrl}", apiEnv);
        await h.WaitForHttpOkAsync(apiUrl, "/health/ready", TimeSpan.FromSeconds(30));

        await ResetInventoryAsync(500m, 0m, cts.Token);

        var termEnvBase = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["POSEDGE_LEASE_TARGET_UNITS_PRODUCT1"] = "5",
            ["POSEDGE_LEASE_TTL_SECONDS"] = "240",
            ["POSEDGE_STARTUP_RECONNECT_JITTER_MS"] = "5000",
            ["POSEDGE_REPLAY_START_JITTER_MS"] = "5000",
            ["POSEDGE_REPLAY_BATCH_SIZE"] = "15",
            ["POSEDGE_JITTER_SEED"] = "22222"
        };

        var terminalIds = new List<Guid>();
        var cashIds = new List<Guid>();
        var replicas = new List<string>();
        for (var i = 0; i < 25; i++)
        {
            var replica = Path.Combine(Path.GetTempPath(), $"posedg-storm25-{i}-{Guid.NewGuid():N}.sqlite");
            var termId = Guid.NewGuid();
            var cashId = Guid.NewGuid();
            terminalIds.Add(termId);
            cashIds.Add(cashId);
            replicas.Add(replica);
        }

        await ProvisionTerminalsAndCashSessionsAsync(terminalIds, cashIds, cts.Token);

        var terminals = new List<(System.Diagnostics.Process Proc, string ReplicaPath)>();
        for (var i = 0; i < 25; i++)
        {
            var env = new Dictionary<string, string>(termEnvBase);
            env["POSEDGE_TEST_DIVERGENT_HOLD_MS"] = "0";
            var p = await h.StartDotnetAsync(terminalProj, $"run -c Release --no-build -- {apiUrl} x \"{replicas[i]}\" {terminalIds[i]:D} {cashIds[i]:D}", env);
            terminals.Add((p, replicas[i]));
        }

        await Task.WhenAll(terminals.Select(t => WaitForActiveLeaseAsync(t.ReplicaPath, TimeSpan.FromSeconds(90), cts.Token)));

        // Kill API to force offline backlog
        ProcessHarness.Kill(api);

        foreach (var (p, _) in terminals)
        {
            for (var j = 0; j < 3; j++)
            {
                await p.StandardInput.WriteLineAsync("sale");
                await p.StandardInput.FlushAsync();
            }
        }

        await Task.Delay(2000, cts.Token);
        Assert.Contains(terminals, t => ReadOutboxCount(t.ReplicaPath) > 0);

        // Restart API, then restart again mid-storm
        api = await h.StartDotnetAsync(apiProj, $"run -c Release --no-build --urls {apiUrl}", apiEnv);
        await h.WaitForHttpOkAsync(apiUrl, "/health/ready", TimeSpan.FromSeconds(30));
        await Task.Delay(1500, cts.Token);
        ProcessHarness.Kill(api);
        await Task.Delay(750, cts.Token);
        api = await h.StartDotnetAsync(apiProj, $"run -c Release --no-build --urls {apiUrl}", apiEnv);
        await h.WaitForHttpOkAsync(apiUrl, "/health/ready", TimeSpan.FromSeconds(30));

        // Optional: restart Postgres too (harder storm)
        await Task.Delay(1000, cts.Token);
        await pg.RestartAsync(cts.Token);

        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cts.Token.ThrowIfCancellationRequested();
            if (terminals.All(t => ReadOutboxCount(t.ReplicaPath) == 0))
                break;
            await Task.Delay(500, cts.Token);
        }

        Assert.All(terminals, t =>
        {
            Assert.Equal(0, ReadOutboxCount(t.ReplicaPath));
            Assert.Equal(0, ReadDeadLetterCount(t.ReplicaPath));
            Assert.Equal(0, ReadInflightCount(t.ReplicaPath));
            var st = ReadHealthState(t.ReplicaPath);
            Assert.DoesNotContain(st, new[] { "divergent", "revoked" });
        });

        var allReqIds = terminals.SelectMany(t => ReadCommittedRequestIds(t.ReplicaPath)).ToList();
        Assert.NotEmpty(allReqIds);
        Assert.Equal(allReqIds.Count, allReqIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var serverCount = await CountSalesByRequestIdsAsync(allReqIds, cts.Token);
        Assert.Equal(allReqIds.Count, serverCount);
        var onHand = await ReadOnHandAsync(cts.Token);
        Assert.True(onHand >= 0m);
    }

    private static async Task ResetInventoryAsync(decimal onHand, decimal reserved, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(Cs);
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

    private static async Task<decimal> ReadOnHandAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(Cs);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          SELECT on_hand
                            FROM inventory
                           WHERE tenant_id = '11111111-1111-1111-1111-111111111111'
                             AND branch_id = '22222222-2222-2222-2222-222222222222'
                             AND product_id = '33333333-3333-3333-3333-333333333333';
                          """;
        var o = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToDecimal(o);
    }

    private static async Task ProvisionTerminalsAndCashSessionsAsync(IReadOnlyList<Guid> terminalIds, IReadOnlyList<Guid> cashIds, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(Cs);
        await conn.OpenAsync(ct);

        // Insert terminals
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                              INSERT INTO terminals(terminal_id, tenant_id, branch_id, name, machine_name, status)
                              SELECT x.terminal_id, '11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222',
                                     x.name, x.machine_name, 'active'
                                FROM (SELECT unnest(@tids::uuid[]) AS terminal_id,
                                             ('Storm-' || unnest(@tids::uuid[])::text) AS name,
                                             ('STORM-' || unnest(@tids::uuid[])::text) AS machine_name) x
                              ON CONFLICT (terminal_id) DO NOTHING;
                              """;
            cmd.Parameters.AddWithValue("@tids", terminalIds.ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Insert cash sessions (opened_by uses admin from seed)
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                              INSERT INTO cash_sessions(cash_session_id, tenant_id, branch_id, terminal_id, opened_by, opening_amount, status)
                              SELECT x.cash_id, '11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222',
                                     x.terminal_id, '99999999-9999-9999-9999-999999999999', 0, 'open'
                                FROM (SELECT unnest(@cids::uuid[]) AS cash_id,
                                             unnest(@tids::uuid[]) AS terminal_id) x
                              ON CONFLICT (cash_session_id) DO NOTHING;
                              """;
            cmd.Parameters.AddWithValue("@cids", cashIds.ToArray());
            cmd.Parameters.AddWithValue("@tids", terminalIds.ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task WaitForActiveLeaseAsync(string sqlitePath, TimeSpan timeout, CancellationToken ct)
    {
        _ = new PosEdge.Terminal.Replica.TerminalReplicaDb(sqlitePath);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate }.ToString();
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM leases WHERE status='active';";
                var c = Convert.ToInt32(cmd.ExecuteScalar());
                if (c > 0) return;
            }
            catch { }
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("Active lease not acquired in time.");
    }

    private static string ReadHealthState(string sqlitePath)
    {
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate }.ToString();
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          SELECT state FROM terminal_health
                           WHERE tenant_id='11111111-1111-1111-1111-111111111111'
                             AND branch_id='22222222-2222-2222-2222-222222222222'
                           LIMIT 1;
                          """;
        return (string?)cmd.ExecuteScalar() ?? "";
    }

    private static List<string> ReadCommittedRequestIds(string sqlitePath)
    {
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate }.ToString();
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT request_id FROM pending_sales WHERE status='committed';";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private static async Task<int> CountSalesByRequestIdsAsync(IReadOnlyCollection<string> requestIds, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(Cs);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          SELECT COUNT(*)
                            FROM sales
                           WHERE tenant_id = '11111111-1111-1111-1111-111111111111'
                             AND branch_id = '22222222-2222-2222-2222-222222222222'
                             AND request_id = ANY(@rids);
                          """;
        cmd.Parameters.AddWithValue("@rids", requestIds.ToArray());
        var o = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(o);
    }

    private static int ReadOutboxCount(string sqlitePath)
    {
        _ = new PosEdge.Terminal.Replica.TerminalReplicaDb(sqlitePath);
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate }.ToString();
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM local_outbox;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static int ReadDeadLetterCount(string sqlitePath)
    {
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate }.ToString();
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dead_letter;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static int ReadInflightCount(string sqlitePath)
    {
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate }.ToString();
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM local_outbox WHERE state='inflight';";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}

