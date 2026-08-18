using Xunit;
using PosEdge.IntegrationTests;
using Npgsql;
using System.Text.Json;

namespace PosEdge.ChaosTests;

public sealed class PostgresRestartDuringReplayTests
{
    [Fact]
    public async Task Postgres_restart_during_replay_eventually_commits_once()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));

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

        // Ensure stock for product1
        await ResetInventoryAsync(50m, 0m, cts.Token);

        // Start terminal B with isolated replica
        var replicaB = Path.Combine(Path.GetTempPath(), "posedg-chaos-replay-b-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var termEnv = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["POSEDGE_LEASE_TARGET_UNITS_PRODUCT1"] = "5",
            ["POSEDGE_LEASE_TTL_SECONDS"] = "120"
        };

        var termB = await h.StartDotnetAsync(terminalProj, $"run -c Release --no-build -- {apiUrl} b \"{replicaB}\"", termEnv);
        _ = termB;

        // Let it connect and acquire lease (required for LEASES_ONLY offline enqueue).
        await WaitForActiveLeaseAsync(replicaB, timeout: TimeSpan.FromSeconds(45), cts.Token);

        // Simulate offline by killing API.
        ProcessHarness.Kill(api);

        for (var i = 0; i < 3; i++)
        {
            await termB.StandardInput.WriteLineAsync("sale");
            await termB.StandardInput.FlushAsync();
            await Task.Delay(250, cts.Token);
        }

        // Confirm local_outbox has pending rows.
        await WaitForOutboxCountAtLeast(replicaB, min: 1, timeout: TimeSpan.FromSeconds(20), cts.Token);

        // Bring API back.
        api = await h.StartDotnetAsync(apiProj, $"run -c Release --no-build --urls {apiUrl}", apiEnv);
        _ = api;
        await h.WaitForHttpOkAsync(apiUrl, "/health/ready", TimeSpan.FromSeconds(30));

        // Restart Postgres during replay window.
        await Task.Delay(1500, cts.Token);
        await pg.RestartAsync(cts.Token);

        // Eventually outbox should drain and no dead-letter.
        await WaitForOutboxCountAtMost(replicaB, max: 0, timeout: TimeSpan.FromSeconds(90), cts.Token);
        Assert.Equal(0, ReadDeadLetterCount(replicaB));
        Assert.Equal(0, ReadInflightCount(replicaB));

        // Exactly-once: sales table must contain exactly the number of unique requestIds in pending_sales committed.
        // We don't have direct rids from stdin, so we read them from SQLite pending_sales.
        var requestIds = ReadPendingSalesRequestIds(replicaB);
        Assert.NotEmpty(requestIds);

        var committedCount = await CountSalesByRequestIdsAsync(requestIds, cts.Token);
        Assert.Equal(requestIds.Count, committedCount);
    }

    private const string Cs = "Host=127.0.0.1;Port=5432;Database=posedgedb;Username=posedge;Password=posedge;Include Error Detail=true";

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

    private static async Task WaitForOutboxCountAtLeast(string sqlitePath, int min, TimeSpan timeout, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadOutboxCount(sqlitePath) >= min) return;
            await Task.Delay(200, ct);
        }
        throw new TimeoutException("outbox did not reach min");
    }

    private static async Task WaitForOutboxCountAtMost(string sqlitePath, int max, TimeSpan timeout, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadOutboxCount(sqlitePath) <= max) return;
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("outbox did not drain");
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

    private static List<string> ReadPendingSalesRequestIds(string sqlitePath)
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
                cmd.CommandText = """
                                  SELECT COUNT(*)
                                    FROM leases l
                                    JOIN lease_lines ll ON ll.lease_id = l.lease_id
                                   WHERE l.status='active';
                                  """;
                var c = Convert.ToInt32(cmd.ExecuteScalar());
                if (c > 0) return;
            }
            catch { }
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("Active lease not acquired in time.");
    }
}

