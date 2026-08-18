using Xunit;
using PosEdge.IntegrationTests;

namespace PosEdge.ChaosTests;

public sealed class PostgresRestartDuringSnapshotTests
{
    [Fact]
    public async Task Postgres_restart_during_snapshot_recovery_eventually_recovers()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var pg = await PostgresControllerFactory.DetectAsync(cts.Token);
        if (pg == null)
            throw new InvalidOperationException("No Postgres controller detected (Windows service / pg_ctl / docker).");

        // We reuse the integration harness patterns: start API+terminal B, force divergence,
        // and restart postgres during recovery window.
        await using var h = new ProcessHarness();
        var port = ProcessHarness.GetFreeTcpPort();
        var apiUrl = $"http://localhost:{port}";

        var apiEnv = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development"
        };
        var repoRoot = ProcessHarness.FindRepoRoot(AppContext.BaseDirectory);
        var apiProj = Path.Combine(repoRoot, "src", "PosEdge.Api");
        var terminalProj = Path.Combine(repoRoot, "src", "PosEdge.Terminal");

        var api = await h.StartDotnetAsync(apiProj, $"run -c Release --no-build --urls {apiUrl}", apiEnv);
        _ = api;
        await h.WaitForHttpOkAsync(apiUrl, "/health/ready", TimeSpan.FromSeconds(30));

        var replicaB = Path.Combine(Path.GetTempPath(), "posedg-chaos-pg-b-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var termEnv = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["POSEDGE_TEST_DIVERGENT_HOLD_MS"] = "500", // small delay; we’ll restart postgres while it’s doing snapshot
            ["POSEDGE_GAP_GRACE_MS"] = "0",            // force fast divergence for chaos test
        };
        var termB = await h.StartDotnetAsync(terminalProj, $"run -c Release --no-build -- {apiUrl} b \"{replicaB}\"", termEnv);
        _ = termB;

        // Ensure schema exists
        _ = new PosEdge.Terminal.Replica.TerminalReplicaDb(replicaB);

        // Force divergence: insert a gap.
        // (Inline helper to avoid sharing private methods.)
        {
            var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = replicaB, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate }.ToString();
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                              INSERT OR IGNORE INTO pending_events(seq, type, at_ms, payload_json, received_at_ms)
                              VALUES (8000, 'Sale.Committed', 0, '{"type":"Sale.Committed"}', 0);
                              """;
            cmd.ExecuteNonQuery();
        }

        // Wait until divergent is observed, then restart postgres during recovery attempts.
        await WaitForStateAsync(replicaB, "divergent", TimeSpan.FromSeconds(20), cts.Token);

        await pg.RestartAsync(cts.Token);

        // Should eventually recover back to online (terminal keeps retrying snapshot).
        await WaitForStateAsync(replicaB, "online", TimeSpan.FromSeconds(60), cts.Token);
    }

    private static async Task WaitForStateAsync(string sqlitePath, string state, TimeSpan timeout, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate }.ToString();
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs);
                await conn.OpenAsync(ct);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                                  SELECT state
                                    FROM terminal_health
                                   WHERE tenant_id='11111111-1111-1111-1111-111111111111'
                                     AND branch_id='22222222-2222-2222-2222-222222222222'
                                     AND terminal_id='bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
                                  """;
                var o = (string?)await cmd.ExecuteScalarAsync(ct);
                if (string.Equals(o, state, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            catch { }
            await Task.Delay(250, ct);
        }
        throw new TimeoutException($"state != {state} after {timeout}");
    }
}

