using Microsoft.EntityFrameworkCore;
using Prometheus;
using PosEdge.Infrastructure;
using System.Data;

namespace PosEdge.Api.Ops;

public static class OpsMetrics
{
    private static readonly Gauge UnresolvedDiscrepancies = Metrics.CreateGauge(
        "unresolved_discrepancies",
        "Count of unresolved cash discrepancies",
        new GaugeConfiguration { LabelNames = new[] { "tenant", "branch" } });

    private static readonly Counter CashDiscrepanciesTotal = Metrics.CreateCounter(
        "cash_discrepancies_total",
        "Total cash discrepancy findings inserted",
        new CounterConfiguration { LabelNames = new[] { "tenant", "branch" } });

    private static readonly Gauge JournalImbalances = Metrics.CreateGauge(
        "journal_imbalances_total",
        "Count of imbalanced journals",
        new GaugeConfiguration { LabelNames = new[] { "tenant", "branch" } });

    private static readonly Gauge ReplayBacklogDepth = Metrics.CreateGauge(
        "replay_backlog_depth",
        "Approximate replay backlog depth (pending outbox commands in replicas not available server-side; this is server outbox pending/dead only)",
        new GaugeConfiguration { LabelNames = new[] { "tenant", "branch", "status" } });

    private static readonly Gauge ReplayOldestAgeMs = Metrics.CreateGauge(
        "replay_oldest_age_ms",
        "Age of oldest pending replay item (ms)",
        new GaugeConfiguration { LabelNames = new[] { "tenant", "branch" } });

    private static readonly Gauge ReplayDeadLetterCount = Metrics.CreateGauge(
        "replay_dead_letter_count",
        "Count of terminal dead-letter items (mirrored)",
        new GaugeConfiguration { LabelNames = new[] { "tenant", "branch" } });

    private static readonly Gauge ReplayStuckInflightTotal = Metrics.CreateGauge(
        "replay_stuck_inflight_total",
        "Count of terminal replay items stuck inflight > 60s",
        new GaugeConfiguration { LabelNames = new[] { "tenant", "branch" } });

    private static readonly Counter ReplayManualRetryTotal = Metrics.CreateCounter(
        "replay_manual_retry_total",
        "Total ops manual replay retry actions",
        new CounterConfiguration { LabelNames = new[] { "tenant", "branch" } });

    private static readonly Counter ReplayPurgeTotal = Metrics.CreateCounter(
        "replay_purge_total",
        "Total ops replay purge actions",
        new CounterConfiguration { LabelNames = new[] { "tenant", "branch" } });

    private static readonly Counter ReplayRequeueTotal = Metrics.CreateCounter(
        "replay_requeue_total",
        "Total ops replay requeue actions",
        new CounterConfiguration { LabelNames = new[] { "tenant", "branch" } });

    private static readonly Gauge ActiveTerminals = Metrics.CreateGauge(
        "active_terminals",
        "Terminals with recent heartbeat/push",
        new GaugeConfiguration { LabelNames = new[] { "tenant", "branch" } });

    private static readonly Gauge OfflineTerminals = Metrics.CreateGauge(
        "offline_terminals",
        "Terminals considered offline/stale",
        new GaugeConfiguration { LabelNames = new[] { "tenant", "branch" } });

    private static readonly Gauge DivergentTerminals = Metrics.CreateGauge(
        "divergent_terminals",
        "Terminals in divergent state",
        new GaugeConfiguration { LabelNames = new[] { "tenant", "branch" } });

    private static readonly Gauge ReplayingTerminals = Metrics.CreateGauge(
        "replaying_terminals",
        "Terminals replaying (or with backlog)",
        new GaugeConfiguration { LabelNames = new[] { "tenant", "branch" } });

    private static readonly Gauge DegradedTerminals = Metrics.CreateGauge(
        "degraded_terminals",
        "Terminals in degraded state",
        new GaugeConfiguration { LabelNames = new[] { "tenant", "branch" } });

    private static readonly Gauge RevokedTerminals = Metrics.CreateGauge(
        "revoked_terminals",
        "Revoked terminals",
        new GaugeConfiguration { LabelNames = new[] { "tenant", "branch" } });

    public static async Task UpdateOnceAsync(PosEdgeDbContext db, CancellationToken ct)
    {
        // MVP: demo tenant/branch only
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var branchId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var t = tenantId.ToString("N");
        var b = branchId.ToString("N");

        var unresolved = await db.CashSessions.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.BranchId == branchId)
            .Where(s => s.ReconciliationStatus == "discrepancy_open")
            .CountAsync(ct);
        UnresolvedDiscrepancies.WithLabels(t, b).Set(unresolved);

        var imbalanced = await db.FinancialJournal.AsNoTracking()
            .Where(j => j.TenantId == tenantId && j.BranchId == branchId && j.TotalDebit != j.TotalCredit)
            .CountAsync(ct);
        JournalImbalances.WithLabels(t, b).Set(imbalanced);

        var outboxPending = await db.Outbox.AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.BranchId == branchId && o.Status == "pending")
            .CountAsync(ct);
        var outboxDead = await db.Outbox.AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.BranchId == branchId && o.Status == "dead")
            .CountAsync(ct);
        ReplayBacklogDepth.WithLabels(t, b, "pending").Set(outboxPending);
        ReplayBacklogDepth.WithLabels(t, b, "dead").Set(outboxDead);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Avoid EF SqlQuery() wrapper quirks with scalar aggregation; use ExecuteScalar via current connection.
        long? oldest;
        await using (var cmd = db.Database.GetDbConnection().CreateCommand())
        {
            cmd.CommandText = """
                              SELECT MIN(created_at_ms)
                                FROM terminal_replay_items
                               WHERE tenant_id = @t AND branch_id = @b AND state IN ('pending','retry_wait');
                              """;
            var p1 = cmd.CreateParameter(); p1.ParameterName = "@t"; p1.Value = tenantId; cmd.Parameters.Add(p1);
            var p2 = cmd.CreateParameter(); p2.ParameterName = "@b"; p2.Value = branchId; cmd.Parameters.Add(p2);
            if (cmd.Connection!.State != System.Data.ConnectionState.Open)
                await cmd.Connection.OpenAsync(ct);
            var o = await cmd.ExecuteScalarAsync(ct);
            oldest = (o == null || o is DBNull) ? null : Convert.ToInt64(o);
        }
        ReplayOldestAgeMs.WithLabels(t, b).Set(oldest.HasValue ? (now - oldest.Value) : 0);

        int deadLetterCount;
        await using (var cmd = db.Database.GetDbConnection().CreateCommand())
        {
            cmd.CommandText = """
                              SELECT COUNT(*) FROM terminal_dead_letters WHERE tenant_id = @t AND branch_id = @b;
                              """;
            var p1 = cmd.CreateParameter(); p1.ParameterName = "@t"; p1.Value = tenantId; cmd.Parameters.Add(p1);
            var p2 = cmd.CreateParameter(); p2.ParameterName = "@b"; p2.Value = branchId; cmd.Parameters.Add(p2);
            if (cmd.Connection!.State != ConnectionState.Open)
                await cmd.Connection.OpenAsync(ct);
            var o = await cmd.ExecuteScalarAsync(ct);
            deadLetterCount = Convert.ToInt32(o ?? 0);
        }
        ReplayDeadLetterCount.WithLabels(t, b).Set(deadLetterCount);

        int stuckInflight;
        await using (var cmd = db.Database.GetDbConnection().CreateCommand())
        {
            cmd.CommandText = """
                              SELECT COUNT(*) FROM terminal_replay_items
                               WHERE tenant_id = @t AND branch_id = @b
                                 AND state='inflight'
                                 AND inflight_at_ms IS NOT NULL
                                 AND inflight_at_ms <= @cut;
                              """;
            var p1 = cmd.CreateParameter(); p1.ParameterName = "@t"; p1.Value = tenantId; cmd.Parameters.Add(p1);
            var p2 = cmd.CreateParameter(); p2.ParameterName = "@b"; p2.Value = branchId; cmd.Parameters.Add(p2);
            var p3 = cmd.CreateParameter(); p3.ParameterName = "@cut"; p3.Value = now - 60000; cmd.Parameters.Add(p3);
            if (cmd.Connection!.State != ConnectionState.Open)
                await cmd.Connection.OpenAsync(ct);
            var o = await cmd.ExecuteScalarAsync(ct);
            stuckInflight = Convert.ToInt32(o ?? 0);
        }
        ReplayStuckInflightTotal.WithLabels(t, b).Set(stuckInflight);

        int manualRetry;
        await using (var cmd = db.Database.GetDbConnection().CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM ops_replay_actions WHERE tenant_id=@t AND branch_id=@b AND action='retry';";
            var p1 = cmd.CreateParameter(); p1.ParameterName = "@t"; p1.Value = tenantId; cmd.Parameters.Add(p1);
            var p2 = cmd.CreateParameter(); p2.ParameterName = "@b"; p2.Value = branchId; cmd.Parameters.Add(p2);
            if (cmd.Connection!.State != ConnectionState.Open)
                await cmd.Connection.OpenAsync(ct);
            var o = await cmd.ExecuteScalarAsync(ct);
            manualRetry = Convert.ToInt32(o ?? 0);
        }
        ReplayManualRetryTotal.WithLabels(t, b).IncTo(manualRetry);

        int purge;
        await using (var cmd = db.Database.GetDbConnection().CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM ops_replay_actions WHERE tenant_id=@t AND branch_id=@b AND action='purge';";
            var p1 = cmd.CreateParameter(); p1.ParameterName = "@t"; p1.Value = tenantId; cmd.Parameters.Add(p1);
            var p2 = cmd.CreateParameter(); p2.ParameterName = "@b"; p2.Value = branchId; cmd.Parameters.Add(p2);
            if (cmd.Connection!.State != ConnectionState.Open)
                await cmd.Connection.OpenAsync(ct);
            var o = await cmd.ExecuteScalarAsync(ct);
            purge = Convert.ToInt32(o ?? 0);
        }
        ReplayPurgeTotal.WithLabels(t, b).IncTo(purge);

        int requeue;
        await using (var cmd = db.Database.GetDbConnection().CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM ops_replay_actions WHERE tenant_id=@t AND branch_id=@b AND action='requeue';";
            var p1 = cmd.CreateParameter(); p1.ParameterName = "@t"; p1.Value = tenantId; cmd.Parameters.Add(p1);
            var p2 = cmd.CreateParameter(); p2.ParameterName = "@b"; p2.Value = branchId; cmd.Parameters.Add(p2);
            if (cmd.Connection!.State != ConnectionState.Open)
                await cmd.Connection.OpenAsync(ct);
            var o = await cmd.ExecuteScalarAsync(ct);
            requeue = Convert.ToInt32(o ?? 0);
        }
        ReplayRequeueTotal.WithLabels(t, b).IncTo(requeue);

        // cash_discrepancies_total as "ever observed" via reconciliation_findings is not perfect; keep it a gauge-like counter bump:
        // (We don't have a monotonic source without extra state table; keep it as scrape-time count for now.)
        int discCount;
        await using (var cmd = db.Database.GetDbConnection().CreateCommand())
        {
            cmd.CommandText = """
                              SELECT COUNT(*)
                                FROM cash_discrepancies
                               WHERE tenant_id = @t AND branch_id = @b;
                              """;
            var p1 = cmd.CreateParameter(); p1.ParameterName = "@t"; p1.Value = tenantId; cmd.Parameters.Add(p1);
            var p2 = cmd.CreateParameter(); p2.ParameterName = "@b"; p2.Value = branchId; cmd.Parameters.Add(p2);
            if (cmd.Connection!.State != ConnectionState.Open)
                await cmd.Connection.OpenAsync(ct);
            var o = await cmd.ExecuteScalarAsync(ct);
            discCount = Convert.ToInt32(o ?? 0);
        }
        CashDiscrepanciesTotal.WithLabels(t, b).IncTo(discCount);

        // Fleet status from terminal_status (server-side derived).
        // Use a 15s "stale" window for metrics.
        var staleCut = DateTimeOffset.UtcNow.AddSeconds(-15);
        // terminal_status isn't currently mapped in EF; query via SQL.
        // Avoid SqlQuery scalar wrapper issues; use ExecuteScalar.
        var activeTerminals = await ScalarIntAsync(db, """
            SELECT COUNT(*)
              FROM terminal_status
             WHERE tenant_id = @t AND branch_id = @b
               AND last_seen_at IS NOT NULL AND last_seen_at >= @cut;
            """, tenantId, branchId, staleCut, ct);
        var offlineTerminals = await ScalarIntAsync(db, """
            SELECT COUNT(*)
              FROM terminal_status
             WHERE tenant_id = @t AND branch_id = @b
               AND (last_seen_at IS NULL OR last_seen_at < @cut);
            """, tenantId, branchId, staleCut, ct);
        var divergentTerminals = await ScalarIntAsync(db, """
            SELECT COUNT(*)
              FROM terminal_status
             WHERE tenant_id = @t AND branch_id = @b
               AND health_state = 'divergent';
            """, tenantId, branchId, staleCut, ct);
        var replayingTerminals = await ScalarIntAsync(db, """
            SELECT COUNT(*)
              FROM terminal_status
             WHERE tenant_id = @t AND branch_id = @b
               AND (COALESCE(replay_backlog,0) > 0 OR health_state = 'replaying');
            """, tenantId, branchId, staleCut, ct);
        var degradedTerminals = await ScalarIntAsync(db, """
            SELECT COUNT(*)
              FROM terminal_status
             WHERE tenant_id = @t AND branch_id = @b
               AND (health_state = 'degraded' OR health_state = 'recovering_degraded');
            """, tenantId, branchId, staleCut, ct);
        var revokedTerminals = await ScalarIntAsync(db, """
            SELECT COUNT(*)
              FROM terminal_status
             WHERE tenant_id = @t AND branch_id = @b
               AND health_state = 'revoked';
            """, tenantId, branchId, staleCut, ct);

        ActiveTerminals.WithLabels(t, b).Set(activeTerminals);
        OfflineTerminals.WithLabels(t, b).Set(offlineTerminals);
        DivergentTerminals.WithLabels(t, b).Set(divergentTerminals);
        ReplayingTerminals.WithLabels(t, b).Set(replayingTerminals);
        DegradedTerminals.WithLabels(t, b).Set(degradedTerminals);
        RevokedTerminals.WithLabels(t, b).Set(revokedTerminals);
    }

    private static async Task<int> ScalarIntAsync(PosEdgeDbContext db, string sql, Guid tenantId, Guid branchId, DateTimeOffset cut, CancellationToken ct)
    {
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        var p1 = cmd.CreateParameter(); p1.ParameterName = "@t"; p1.Value = tenantId; cmd.Parameters.Add(p1);
        var p2 = cmd.CreateParameter(); p2.ParameterName = "@b"; p2.Value = branchId; cmd.Parameters.Add(p2);
        var p3 = cmd.CreateParameter(); p3.ParameterName = "@cut"; p3.Value = cut; cmd.Parameters.Add(p3);
        if (cmd.Connection!.State != ConnectionState.Open)
            await cmd.Connection.OpenAsync(ct);
        var o = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(o ?? 0);
    }

    private static void IncTo(this Counter c, double value)
    {
        // prometheus-net counters can't set; simulate by incrementing from 0 per process lifetime.
        // This is acceptable for dev; production should store last value in memory+persist if needed.
        var current = _counterCache.GetValueOrDefault(c);
        if (value > current)
        {
            c.Inc(value - current);
            _counterCache[c] = value;
        }
    }

    private static readonly Dictionary<Counter, double> _counterCache = new();
}

