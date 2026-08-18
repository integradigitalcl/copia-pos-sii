using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Xunit;

namespace PosEdge.IntegrationTests;

public sealed class EndToEndTests
{
    private static readonly string RepoRoot = ProcessHarness.FindRepoRoot(AppContext.BaseDirectory);
    private static readonly string ApiProj = Path.Combine(RepoRoot, "src", "PosEdge.Api");
    private static readonly string TerminalProj = Path.Combine(RepoRoot, "src", "PosEdge.Terminal");

    [Fact]
    public async Task Offline_replay_basic_two_terminals()
    {
        await using var h = new ProcessHarness();

        var port = ProcessHarness.GetFreeTcpPort();
        var apiUrl = $"http://localhost:{port}";

        // Start API
        var apiEnv = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development"
        };
        var api = await h.StartDotnetAsync(ApiProj, $"run -c Release --no-build --urls {apiUrl}", apiEnv);
        _ = api; // keep for lifetime
        await h.WaitForHttpOkAsync(apiUrl, "/health/live", TimeSpan.FromSeconds(20));
        await h.WaitForHttpOkAsync(apiUrl, "/health/ready", TimeSpan.FromSeconds(20));

        // Ensure stock for product1 so Sale.Commit won't fail due to previous runs.
        await DbReset.ResetInventoryAsync(onHand: 50m, reserved: 0m);

        // Start terminals A + B
        var replicaA = Path.Combine(Path.GetTempPath(), "posedg-e2e-a-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var replicaB = Path.Combine(Path.GetTempPath(), "posedg-e2e-b-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var termA = await h.StartDotnetAsync(TerminalProj, $"run -c Release --no-build -- {apiUrl} a \"{replicaA}\"");
        var termB = await h.StartDotnetAsync(TerminalProj, $"run -c Release --no-build -- {apiUrl} b \"{replicaB}\"");
        _ = termA;

        // Let them connect and acquire leases
        await Task.Delay(2500);

        // Kill terminal B to simulate offline
        ProcessHarness.Kill(termB);

        // Trigger a sale from A by typing "sale" into stdin isn't possible with current terminal app,
        // so we commit directly via API as terminal A identity. This still produces events for sync/replay.
        var body = new
        {
            tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            branchId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            terminalId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            cashSessionId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            requestId = "E2E-" + Guid.NewGuid().ToString("N"),
            currency = "MXN",
            discountTotal = 0m,
            leaseId = (Guid?)null, // online sale
            lines = new[]
            {
                new { productId = Guid.Parse("33333333-3333-3333-3333-333333333333"), sku = "SKU-COCA-600", name = "Refresco 600ml", qty = 1m, unitPrice = 18.50m, taxRate = 0.16m }
            },
            payments = new[]
            {
                new { method = "cash", amount = 20m, currency = "MXN", meta = (object?)null }
            }
        };
        var r = await JsonHttp.PostJsonAsync(apiUrl, "/v1/sales/commit", body);
        Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString());
        var eventSeq = r.TryGetProperty("eventSeq", out var es) && es.ValueKind == System.Text.Json.JsonValueKind.Number
            ? es.GetInt64()
            : 0;
        Assert.True(eventSeq > 0, "eventSeq missing/invalid in response: " + r);

        // Restart B and allow sync loop to pull missed event
        termB = await h.StartDotnetAsync(TerminalProj, $"run -c Release --no-build -- {apiUrl} b \"{replicaB}\"");
        await WaitForTerminalAppliedSeqAsync(replicaB,
            tenantId: "11111111-1111-1111-1111-111111111111",
            branchId: "22222222-2222-2222-2222-222222222222",
            terminalId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            minSeq: eventSeq,
            timeout: TimeSpan.FromSeconds(60));

        // Minimal assertion: API sync from seq=0 returns at least one event (the sale)
        var sync = await JsonHttp.GetJsonAsync(apiUrl, "/v1/sync?tenantId=11111111-1111-1111-1111-111111111111&branchId=22222222-2222-2222-2222-222222222222&fromSeq=0&limit=10");
        Assert.True(sync.TryGetProperty("events", out var evs) && evs.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Divergence_recovery_full_resync_end_to_end()
    {
        await using var h = new ProcessHarness();

        var port = ProcessHarness.GetFreeTcpPort();
        var apiUrl = $"http://localhost:{port}";

        var apiEnv = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development"
        };
        var api = await h.StartDotnetAsync(ApiProj, $"run -c Release --no-build --urls {apiUrl}", apiEnv);
        _ = api;
        await h.WaitForHttpOkAsync(apiUrl, "/health/live", TimeSpan.FromSeconds(20));
        await h.WaitForHttpOkAsync(apiUrl, "/health/ready", TimeSpan.FromSeconds(20));

        await DbReset.ResetInventoryAsync(onHand: 50m, reserved: 0m);

        var replicaA = Path.Combine(Path.GetTempPath(), "posedg-div-a-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var replicaB = Path.Combine(Path.GetTempPath(), "posedg-div-b-" + Guid.NewGuid().ToString("N") + ".sqlite");

        var termEnv = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["POSEDGE_TEST_DIVERGENT_HOLD_MS"] = "2000", // allow us to observe divergent state before recovery starts
            ["POSEDGE_DIVERGENCE_CHECK_SECONDS"] = "1"
        };

        var termA = await h.StartDotnetAsync(TerminalProj, $"run -c Release --no-build -- {apiUrl} a \"{replicaA}\"", termEnv);
        var termB = await h.StartDotnetAsync(TerminalProj, $"run -c Release --no-build -- {apiUrl} b \"{replicaB}\"", termEnv);
        _ = termA; _ = termB;

        // Generate a committed sale to create non-zero base seq and inventory cache updates.
        var body = new
        {
            tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            branchId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            terminalId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            cashSessionId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            requestId = "DIV-" + Guid.NewGuid().ToString("N"),
            currency = "MXN",
            discountTotal = 0m,
            leaseId = (Guid?)null,
            lines = new[]
            {
                new { productId = Guid.Parse("33333333-3333-3333-3333-333333333333"), sku = "SKU-COCA-600", name = "Refresco 600ml", qty = 1m, unitPrice = 18.50m, taxRate = 0.16m }
            },
            payments = new[]
            {
                new { method = "cash", amount = 20m, currency = "MXN", meta = (object?)null }
            }
        };
        var commit = await JsonHttp.PostJsonAsync(apiUrl, "/v1/sales/commit", body);
        Assert.True(commit.GetProperty("ok").GetBoolean(), commit.ToString());
        var eventSeq = commit.GetProperty("eventSeq").GetInt64();
        Assert.True(eventSeq > 0);

        // Wait until B is synced to this seq (normal sync working).
        await WaitForTerminalAppliedSeqAsync(replicaB,
            tenantId: "11111111-1111-1111-1111-111111111111",
            branchId: "22222222-2222-2222-2222-222222222222",
            terminalId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            minSeq: eventSeq,
            timeout: TimeSpan.FromSeconds(20));

        // Corrupt B: inventory_cache value for product1.
        CorruptInventoryCache(replicaB, productId: "33333333-3333-3333-3333-333333333333");

        // Wait for divergent state to be written.
        await WaitForTerminalHealthStateAsync(replicaB,
            tenantId: "11111111-1111-1111-1111-111111111111",
            branchId: "22222222-2222-2222-2222-222222222222",
            terminalId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            state: "divergent",
            timeout: TimeSpan.FromSeconds(15));

        // Then wait for recovery to complete and return online.
        await WaitForTerminalHealthStateAsync(replicaB,
            tenantId: "11111111-1111-1111-1111-111111111111",
            branchId: "22222222-2222-2222-2222-222222222222",
            terminalId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            state: "online",
            timeout: TimeSpan.FromSeconds(30));

        // Validate hashes now match server (hashOnly snapshot)
        var snapHash = await JsonHttp.GetJsonAsync(apiUrl, "/v1/snapshot?tenantId=11111111-1111-1111-1111-111111111111&branchId=22222222-2222-2222-2222-222222222222&hashOnly=true&includeLeases=true");
        var serverBaseSeq = snapHash.GetProperty("baseSeq").GetInt64();
        var serverSnapshotHash = snapHash.GetProperty("snapshotHashNoLeases").GetString();
        var localSnapshotHash = ComputeLocalSnapshotHashNoLeases(replicaB, serverBaseSeq);
        Assert.Equal(serverSnapshotHash, localSnapshotHash);
    }

    [Fact]
    public async Task Divergence_snapshot_interruption_kill_during_hold_then_restart_recovers()
    {
        await using var h = new ProcessHarness();
        var port = ProcessHarness.GetFreeTcpPort();
        var apiUrl = $"http://localhost:{port}";
        var apiEnv = new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Development", ["DOTNET_ENVIRONMENT"] = "Development" };
        var api = await h.StartDotnetAsync(ApiProj, $"run -c Release --no-build --urls {apiUrl}", apiEnv);
        _ = api;
        await h.WaitForHttpOkAsync(apiUrl, "/health/ready", TimeSpan.FromSeconds(20));

        await DbReset.ResetInventoryAsync(onHand: 50m, reserved: 0m);

        var replicaB = Path.Combine(Path.GetTempPath(), "posedg-div-int-b-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var termEnv = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["POSEDGE_TEST_DIVERGENT_HOLD_MS"] = "8000", // long hold so we can kill while preparing snapshot
            ["POSEDGE_DIVERGENCE_CHECK_SECONDS"] = "1", // tighten loop for test determinism
            ["POSEDGE_TEST_FORCE_DIVERGENT"] = "1"
        };

        var termB = await h.StartDotnetAsync(TerminalProj, $"run -c Release --no-build -- {apiUrl} b \"{replicaB}\"", termEnv);
        _ = termB;

        // Ensure schema exists before corruption injection
        _ = new PosEdge.Terminal.Replica.TerminalReplicaDb(replicaB);

        // Force divergence deterministically
        InsertPendingGap(replicaB, seq: 9999);

        await WaitForTerminalHealthStateAsync(replicaB,
            tenantId: "11111111-1111-1111-1111-111111111111",
            branchId: "22222222-2222-2222-2222-222222222222",
            terminalId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            state: "divergent",
            timeout: TimeSpan.FromSeconds(30));

        // Kill during hold (before snapshot apply)
        ProcessHarness.Kill(termB);

        // Restart with no hold so it can recover quickly.
        termEnv["POSEDGE_TEST_DIVERGENT_HOLD_MS"] = "0";
        termB = await h.StartDotnetAsync(TerminalProj, $"run -c Release --no-build -- {apiUrl} b \"{replicaB}\"", termEnv);
        _ = termB;

        await WaitForTerminalHealthStateAsync(replicaB,
            tenantId: "11111111-1111-1111-1111-111111111111",
            branchId: "22222222-2222-2222-2222-222222222222",
            terminalId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            state: "online",
            timeout: TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Divergence_snapshot_apply_failfast_crash_then_restart_recovers()
    {
        await using var h = new ProcessHarness();
        var port = ProcessHarness.GetFreeTcpPort();
        var apiUrl = $"http://localhost:{port}";
        var apiEnv = new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Development", ["DOTNET_ENVIRONMENT"] = "Development" };
        var api = await h.StartDotnetAsync(ApiProj, $"run -c Release --no-build --urls {apiUrl}", apiEnv);
        _ = api;
        await h.WaitForHttpOkAsync(apiUrl, "/health/ready", TimeSpan.FromSeconds(20));

        var replicaB = Path.Combine(Path.GetTempPath(), "posedg-snapff-b-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var termEnv = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["POSEDGE_TEST_DIVERGENT_HOLD_MS"] = "0",
            ["POSEDGE_DIVERGENCE_CHECK_SECONDS"] = "1",
            ["POSEDGE_TEST_SNAPSHOT_FAILFAST_STAGE"] = "3" // crash after staging inventory
        };

        var termB = await h.StartDotnetAsync(TerminalProj, $"run -c Release --no-build -- {apiUrl} b \"{replicaB}\"", termEnv);
        _ = termB;
        _ = new PosEdge.Terminal.Replica.TerminalReplicaDb(replicaB);

        // Force divergence to trigger snapshot path
        InsertPendingGap(replicaB, seq: 9000);

        // Process should crash due to FailFast; wait a moment then restart without failfast.
        await Task.Delay(2000);
        ProcessHarness.Kill(termB);

        termEnv.Remove("POSEDGE_TEST_SNAPSHOT_FAILFAST_STAGE");
        termB = await h.StartDotnetAsync(TerminalProj, $"run -c Release --no-build -- {apiUrl} b \"{replicaB}\"", termEnv);
        _ = termB;

        await WaitForTerminalHealthStateAsync(replicaB,
            tenantId: "11111111-1111-1111-1111-111111111111",
            branchId: "22222222-2222-2222-2222-222222222222",
            terminalId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            state: "online",
            timeout: TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Divergence_gap_recovery_full_resync()
    {
        await using var h = new ProcessHarness();
        var port = ProcessHarness.GetFreeTcpPort();
        var apiUrl = $"http://localhost:{port}";
        var apiEnv = new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Development", ["DOTNET_ENVIRONMENT"] = "Development" };
        var api = await h.StartDotnetAsync(ApiProj, $"run -c Release --no-build --urls {apiUrl}", apiEnv);
        _ = api;
        await h.WaitForHttpOkAsync(apiUrl, "/health/ready", TimeSpan.FromSeconds(20));

        var replicaB = Path.Combine(Path.GetTempPath(), "posedg-gap-b-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var termEnv = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["POSEDGE_TEST_DIVERGENT_HOLD_MS"] = "2000",
            ["POSEDGE_DIVERGENCE_CHECK_SECONDS"] = "1"
        };
        var termB = await h.StartDotnetAsync(TerminalProj, $"run -c Release --no-build -- {apiUrl} b \"{replicaB}\"", termEnv);
        _ = termB;

        // Ensure schema exists before corruption injection
        _ = new PosEdge.Terminal.Replica.TerminalReplicaDb(replicaB);

        // Create irreparable gap by inserting pending seq far ahead.
        InsertPendingGap(replicaB, seq: 5000);

        await WaitForTerminalHealthStateAsync(replicaB,
            tenantId: "11111111-1111-1111-1111-111111111111",
            branchId: "22222222-2222-2222-2222-222222222222",
            terminalId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            state: "online",
            timeout: TimeSpan.FromSeconds(30));
    }

    private static async Task WaitForTerminalAppliedSeqAsync(string sqlitePath, string tenantId, string branchId, string terminalId, long minSeq, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                var cs = new SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
                using var conn = new SqliteConnection(cs);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                                  SELECT last_applied_seq
                                    FROM sync_checkpoint
                                   WHERE tenant_id=$t AND branch_id=$b AND terminal_id=$term;
                                  """;
                cmd.Parameters.AddWithValue("$t", tenantId);
                cmd.Parameters.AddWithValue("$b", branchId);
                cmd.Parameters.AddWithValue("$term", terminalId);
                var o = await cmd.ExecuteScalarAsync();
                var last = o == null ? 0 : Convert.ToInt64(o);
                if (last >= minSeq) return;
            }
            catch { }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Terminal did not apply seq>={minSeq} within {timeout}. sqlite={sqlitePath}");
    }

    private static async Task WaitForTerminalHealthStateAsync(string sqlitePath, string tenantId, string branchId, string terminalId, string state, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate }.ToString();
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                                  SELECT state
                                    FROM terminal_health
                                   WHERE tenant_id=$t AND branch_id=$b AND terminal_id=$term;
                                  """;
                cmd.Parameters.AddWithValue("$t", tenantId);
                cmd.Parameters.AddWithValue("$b", branchId);
                cmd.Parameters.AddWithValue("$term", terminalId);
                var o = (string?)await cmd.ExecuteScalarAsync();
                if (string.Equals(o, state, StringComparison.OrdinalIgnoreCase)) return;
            }
            catch { }
            await Task.Delay(250);
        }
        throw new TimeoutException($"Terminal did not reach state={state} within {timeout}. sqlite={sqlitePath}");
    }

    private static void CorruptInventoryCache(string sqlitePath, string productId)
    {
        _ = new PosEdge.Terminal.Replica.TerminalReplicaDb(sqlitePath);
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate }.ToString();
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          UPDATE inventory_cache
                             SET on_hand = on_hand + 999
                           WHERE product_id=$pid;
                          """;
        cmd.Parameters.AddWithValue("$pid", productId);
        cmd.ExecuteNonQuery();
    }

    private static void InsertPendingGap(string sqlitePath, long seq)
    {
        _ = new PosEdge.Terminal.Replica.TerminalReplicaDb(sqlitePath);
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate }.ToString();
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          INSERT OR IGNORE INTO pending_events(seq, type, at_ms, payload_json, received_at_ms)
                          VALUES ($seq, 'Sale.Committed', 0, '{"type":"Sale.Committed"}', $now);
                          """;
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    private static string ComputeLocalSnapshotHashNoLeases(string sqlitePath, long baseSeq)
    {
        // Mirror terminal local hash computation for verification.
        var db = new PosEdge.Terminal.Replica.TerminalReplicaDb(sqlitePath);
        return db.ComputeLocalHashes(baseSeq).SnapshotHashNoLeases;
    }
}

