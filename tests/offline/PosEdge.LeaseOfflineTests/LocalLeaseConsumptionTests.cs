using PosEdge.Terminal.Offline;
using PosEdge.Terminal.Replica;
using Xunit;

namespace PosEdge.LeaseOfflineTests;

public sealed class LocalLeaseConsumptionTests
{
    [Fact]
    public void Lease_exhaustion_rejects_third_sale_offline()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "posedgetest-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var db = new TerminalReplicaDb(tmp);
        var tenant = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var term = Guid.NewGuid();
        db.EnsureCheckpoint(tenant, branch, term);

        // Seed local lease with alloc=2, used=0 for product1
        var leaseId = Guid.NewGuid().ToString("D");
        var product1 = "33333333-3333-3333-3333-333333333333";
        using (var conn = db.OpenConnection())
        using (var tx = conn.BeginTransaction())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                                  INSERT INTO leases(lease_id, tenant_id, branch_id, terminal_id, created_at_ms, expires_at_ms, status)
                                  VALUES ($lid, $t, $b, $term, $now, $exp, 'active');
                                  """;
                cmd.Parameters.AddWithValue("$lid", leaseId);
                cmd.Parameters.AddWithValue("$t", tenant.ToString("D"));
                cmd.Parameters.AddWithValue("$b", branch.ToString("D"));
                cmd.Parameters.AddWithValue("$term", term.ToString("D"));
                cmd.Parameters.AddWithValue("$now", TerminalReplicaDb.NowMs());
                cmd.Parameters.AddWithValue("$exp", TerminalReplicaDb.NowMs() + 60_000);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                                  INSERT INTO lease_lines(lease_id, product_id, qty_allocated, qty_used)
                                  VALUES ($lid, $pid, 2, 0);
                                  """;
                cmd.Parameters.AddWithValue("$lid", leaseId);
                cmd.Parameters.AddWithValue("$pid", product1);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        var store = new LocalOutboxStore(db, tenant, branch, term);
        string payload1 = $$"""{"lines":[{"productId":"{{product1}}","qty":1}]}""";
        string payload2 = $$"""{"lines":[{"productId":"{{product1}}","qty":1}]}""";
        string payload3 = $$"""{"lines":[{"productId":"{{product1}}","qty":1}]}""";

        store.EnqueueSaleCommit("r1", OfflineMode.LeasesOnly, payload1, leaseId);
        store.EnqueueSaleCommit("r2", OfflineMode.LeasesOnly, payload2, leaseId);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            store.EnqueueSaleCommit("r3", OfflineMode.LeasesOnly, payload3, leaseId));
        Assert.Contains("lease exhausted", ex.Message);
    }

    [Fact]
    public void Duplicate_requestId_does_not_double_consume()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "posedgetest-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var db = new TerminalReplicaDb(tmp);
        var tenant = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var term = Guid.NewGuid();
        db.EnsureCheckpoint(tenant, branch, term);

        var leaseId = Guid.NewGuid().ToString("D");
        var product1 = "33333333-3333-3333-3333-333333333333";
        using (var conn = db.OpenConnection())
        using (var tx = conn.BeginTransaction())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                                  INSERT INTO leases(lease_id, tenant_id, branch_id, terminal_id, created_at_ms, expires_at_ms, status)
                                  VALUES ($lid, $t, $b, $term, $now, $exp, 'active');
                                  """;
                cmd.Parameters.AddWithValue("$lid", leaseId);
                cmd.Parameters.AddWithValue("$t", tenant.ToString("D"));
                cmd.Parameters.AddWithValue("$b", branch.ToString("D"));
                cmd.Parameters.AddWithValue("$term", term.ToString("D"));
                cmd.Parameters.AddWithValue("$now", TerminalReplicaDb.NowMs());
                cmd.Parameters.AddWithValue("$exp", TerminalReplicaDb.NowMs() + 60_000);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                                  INSERT INTO lease_lines(lease_id, product_id, qty_allocated, qty_used)
                                  VALUES ($lid, $pid, 10, 0);
                                  """;
                cmd.Parameters.AddWithValue("$lid", leaseId);
                cmd.Parameters.AddWithValue("$pid", product1);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        var store = new LocalOutboxStore(db, tenant, branch, term);
        var rid = "RID-DUPE";
        string payload = $$"""{"lines":[{"productId":"{{product1}}","qty":2}]}""";

        store.EnqueueSaleCommit(rid, OfflineMode.LeasesOnly, payload, leaseId);
        store.EnqueueSaleCommit(rid, OfflineMode.LeasesOnly, payload, leaseId); // ignored

        // qty_used should be 2 (not 4)
        using var c2 = db.OpenConnection();
        using var cmd2 = c2.CreateCommand();
        cmd2.CommandText = "SELECT qty_used FROM lease_lines WHERE lease_id=$lid AND product_id=$pid;";
        cmd2.Parameters.AddWithValue("$lid", leaseId);
        cmd2.Parameters.AddWithValue("$pid", product1);
        var used = Convert.ToDouble(cmd2.ExecuteScalar());
        Assert.Equal(2.0, used, 6);
    }

    [Fact]
    public void Inflight_crash_is_recovered_to_pending()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "posedgetest-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var db = new TerminalReplicaDb(tmp);
        var tenant = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var term = Guid.NewGuid();
        db.EnsureCheckpoint(tenant, branch, term);

        var store = new LocalOutboxStore(db, tenant, branch, term);
        store.EnqueueSaleCommit("rid1", OfflineMode.Permissive, """{"lines":[{"productId":"33333333-3333-3333-3333-333333333333","qty":1}]}""", null);
        store.MarkInFlight("rid1");

        // Simulate restart recovery
        store.RecoverStuckInFlight(TerminalReplicaDb.NowMs() + 1);

        var batch = store.DequeueBatch(10, TerminalReplicaDb.NowMs());
        Assert.Contains(batch, x => x.RequestId == "rid1");
    }
}

