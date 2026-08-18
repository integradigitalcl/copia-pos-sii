using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PosEdge.Terminal.Replica;

public sealed class TerminalReplicaDb
{
    private readonly string _cs;

    public TerminalReplicaDb(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var b = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _cs = b.ToString();
        EnsureSchema();
    }

    public SqliteConnection OpenConnection()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
                          PRAGMA journal_mode=WAL;
                          PRAGMA synchronous=FULL;
                          PRAGMA foreign_keys=ON;
                          PRAGMA busy_timeout=5000;
                          PRAGMA temp_store=MEMORY;
                          """;
        cmd.ExecuteNonQuery();
        return c;
    }

    public void WithWriteRetry(Action<SqliteConnection> action, string op, int maxAttempts = 6)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var conn = OpenConnection();
                action(conn);
                return;
            }
            catch (SqliteException ex) when (IsBusyOrLocked(ex) && attempt < maxAttempts)
            {
                var backoff = ComputeBackoffMs(attempt);
                AppendReplayLog("sqlite_lock_contention", $"{op} attempt={attempt} backoffMs={backoff} err={ex.SqliteErrorCode}");
                Thread.Sleep(backoff);
            }
        }
    }

    public T WithWriteRetry<T>(Func<SqliteConnection, T> func, string op, int maxAttempts = 6)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var conn = OpenConnection();
                return func(conn);
            }
            catch (SqliteException ex) when (IsBusyOrLocked(ex) && attempt < maxAttempts)
            {
                var backoff = ComputeBackoffMs(attempt);
                AppendReplayLog("sqlite_lock_contention", $"{op} attempt={attempt} backoffMs={backoff} err={ex.SqliteErrorCode}");
                Thread.Sleep(backoff);
            }
        }

        using var connFinal = OpenConnection();
        return func(connFinal);
    }

    private static bool IsBusyOrLocked(SqliteException ex) => ex.SqliteErrorCode is 5 or 6;
    private static int ComputeBackoffMs(int attempt)
    {
        // 25, 50, 100, 200, 400, 800 (+ jitter), bounded
        var baseMs = 25 * (1 << Math.Min(5, attempt - 1));
        var jitter = Random.Shared.Next(0, 25 * attempt);
        return Math.Min(1500, baseMs + jitter);
    }

    private void EnsureSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
        -- ===== Sync state =====
        CREATE TABLE IF NOT EXISTS sync_checkpoint (
          tenant_id TEXT NOT NULL,
          branch_id TEXT NOT NULL,
          terminal_id TEXT NOT NULL,
          last_applied_seq INTEGER NOT NULL DEFAULT 0,
          updated_at_ms INTEGER NOT NULL,
          PRIMARY KEY (tenant_id, branch_id, terminal_id)
        );

        -- Raw received events (buffer for gaps/out-of-order)
        CREATE TABLE IF NOT EXISTS pending_events (
          seq INTEGER PRIMARY KEY,
          type TEXT NOT NULL,
          at_ms INTEGER NOT NULL,
          payload_json TEXT NOT NULL,
          received_at_ms INTEGER NOT NULL
        );

        -- Applied events (dedup + audit)
        CREATE TABLE IF NOT EXISTS applied_events (
          seq INTEGER PRIMARY KEY,
          type TEXT NOT NULL,
          at_ms INTEGER NOT NULL,
          payload_json TEXT NOT NULL,
          applied_at_ms INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_applied_events_type ON applied_events(type, seq);

        -- ===== Caches =====
        CREATE TABLE IF NOT EXISTS product_cache (
          product_id TEXT PRIMARY KEY,
          sku TEXT NOT NULL,
          name TEXT NOT NULL,
          price REAL NOT NULL,
          tax_rate REAL NOT NULL,
          updated_at_ms INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_product_cache_sku ON product_cache(sku);

        CREATE TABLE IF NOT EXISTS inventory_cache (
          product_id TEXT PRIMARY KEY,
          on_hand REAL NOT NULL,
          reserved REAL NOT NULL,
          updated_at_ms INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_inventory_cache_updated ON inventory_cache(updated_at_ms);

        -- ===== Offline queue / local outbox =====
        CREATE TABLE IF NOT EXISTS local_outbox (
          request_id TEXT PRIMARY KEY,
          type TEXT NOT NULL,
          payload_json TEXT NOT NULL,
          created_at_ms INTEGER NOT NULL,
          state TEXT NOT NULL,              -- pending | inflight | dead
          inflight_at_ms INTEGER NULL,
          attempt_count INTEGER NOT NULL DEFAULT 0,
          last_error TEXT NULL,
          next_retry_at_ms INTEGER NULL
        );
        CREATE INDEX IF NOT EXISTS ix_local_outbox_state_retry ON local_outbox(state, next_retry_at_ms);

        -- Pending sales (human-friendly view; generated from local_outbox where type='Sale.Commit')
        CREATE TABLE IF NOT EXISTS pending_sales (
          request_id TEXT PRIMARY KEY,
          created_at_ms INTEGER NOT NULL,
          offline_mode TEXT NOT NULL,       -- strict | leases_only | permissive
          lease_id TEXT NULL,
          status TEXT NOT NULL,             -- pending | committed | rejected | dead
          last_server_code TEXT NULL,
          last_error TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_pending_sales_status ON pending_sales(status, created_at_ms);

        -- Applied commands (dedup for outbox replay, independent from server event seq)
        CREATE TABLE IF NOT EXISTS applied_commands (
          request_id TEXT PRIMARY KEY,
          type TEXT NOT NULL,
          at_ms INTEGER NOT NULL,
          server_code TEXT NULL,
          server_ref_id TEXT NULL
        );

        -- Minimal committed sale cache (needed for offline refund/void flows in burn-in)
        CREATE TABLE IF NOT EXISTS committed_sales (
          request_id TEXT PRIMARY KEY,
          sale_id TEXT NOT NULL,
          terminal_id TEXT NOT NULL,
          cash_session_id TEXT NOT NULL,
          created_at_ms INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_committed_sales_time ON committed_sales(created_at_ms DESC);

        -- ===== Leases (for LEASES_ONLY mode) =====
        CREATE TABLE IF NOT EXISTS leases (
          lease_id TEXT PRIMARY KEY,
          tenant_id TEXT NOT NULL,
          branch_id TEXT NOT NULL,
          terminal_id TEXT NOT NULL,
          created_at_ms INTEGER NOT NULL,
          expires_at_ms INTEGER NOT NULL,
          status TEXT NOT NULL               -- active | expired | revoked
        );
        CREATE INDEX IF NOT EXISTS ix_leases_active ON leases(status, expires_at_ms);

        CREATE TABLE IF NOT EXISTS lease_lines (
          lease_id TEXT NOT NULL,
          product_id TEXT NOT NULL,
          qty_allocated REAL NOT NULL,
          qty_used REAL NOT NULL DEFAULT 0,
          PRIMARY KEY (lease_id, product_id),
          FOREIGN KEY (lease_id) REFERENCES leases(lease_id) ON DELETE CASCADE
        );

        -- ===== Replay/audit log =====
        CREATE TABLE IF NOT EXISTS replay_log (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          at_ms INTEGER NOT NULL,
          kind TEXT NOT NULL,
          request_id TEXT NULL,
          message TEXT NOT NULL,
          meta_json TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_replay_log_time ON replay_log(at_ms DESC);

        -- ===== Dead-letter (permanent failures) =====
        CREATE TABLE IF NOT EXISTS dead_letter (
          request_id TEXT PRIMARY KEY,
          type TEXT NOT NULL,
          dead_reason TEXT NOT NULL,          -- LEASE_REVOKED | LEASE_EXPIRED | INVALID_TERMINAL | INVALID_PAYLOAD | SERVER_REJECTED | ...
          classification TEXT NOT NULL,       -- permanent | security | consistency
          failed_at_ms INTEGER NOT NULL,
          retry_count INTEGER NOT NULL DEFAULT 0,
          last_server_code TEXT NULL,
          last_error TEXT NULL,
          payload_snapshot_json TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_dead_letter_failed ON dead_letter(failed_at_ms DESC);
        CREATE INDEX IF NOT EXISTS ix_dead_letter_reason ON dead_letter(dead_reason, failed_at_ms DESC);

        -- ===== Terminal health =====
        CREATE TABLE IF NOT EXISTS terminal_health (
          tenant_id TEXT NOT NULL,
          branch_id TEXT NOT NULL,
          terminal_id TEXT NOT NULL,
          state TEXT NOT NULL,                -- online|degraded|offline|replaying|divergent|revoked
          updated_at_ms INTEGER NOT NULL,
          last_server_seq INTEGER NULL,
          last_applied_seq INTEGER NULL,
          last_error TEXT NULL,
          meta_json TEXT NULL,
          PRIMARY KEY (tenant_id, branch_id, terminal_id)
        );

        -- ===== Local hash checkpoint (for divergence testing/forensics) =====
        CREATE TABLE IF NOT EXISTS hash_checkpoint (
          tenant_id TEXT NOT NULL,
          branch_id TEXT NOT NULL,
          terminal_id TEXT NOT NULL,
          base_seq INTEGER NOT NULL,
          inventory_hash TEXT NOT NULL,
          products_hash TEXT NOT NULL,
          leases_hash TEXT NOT NULL,
          snapshot_hash TEXT NOT NULL,
          updated_at_ms INTEGER NOT NULL,
          PRIMARY KEY (tenant_id, branch_id, terminal_id)
        );

        -- ===== Terminal session state =====
        CREATE TABLE IF NOT EXISTS terminal_sessions (
          session_id TEXT PRIMARY KEY,
          started_at_ms INTEGER NOT NULL,
          ended_at_ms INTEGER NULL,
          last_server_time_ms INTEGER NULL,
          state TEXT NOT NULL,
          meta_json TEXT NULL
        );
        """;
        cmd.ExecuteNonQuery();

        // Lightweight "migration": add columns if missing.
        EnsureColumnExists(conn, "local_outbox", "inflight_at_ms", "INTEGER NULL");

        // Ensure terminal_health exists for current terminal usage.
        // (No columns to migrate yet.)
    }

    private static void EnsureColumnExists(SqliteConnection conn, string table, string column, string columnSql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var name = r.GetString(1);
            if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                return;
        }

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnSql};";
        alter.ExecuteNonQuery();
    }

    public void EnsureCheckpoint(Guid tenantId, Guid branchId, Guid terminalId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          INSERT OR IGNORE INTO sync_checkpoint(tenant_id, branch_id, terminal_id, last_applied_seq, updated_at_ms)
                          VALUES ($t, $b, $term, 0, $now);
                          """;
        cmd.Parameters.AddWithValue("$t", tenantId.ToString("D"));
        cmd.Parameters.AddWithValue("$b", branchId.ToString("D"));
        cmd.Parameters.AddWithValue("$term", terminalId.ToString("D"));
        cmd.Parameters.AddWithValue("$now", NowMs());
        cmd.ExecuteNonQuery();

        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = """
                           INSERT OR IGNORE INTO terminal_health(tenant_id, branch_id, terminal_id, state, updated_at_ms, last_server_seq, last_applied_seq)
                           VALUES ($t, $b, $term, 'offline', $now, NULL, 0);
                           """;
        cmd2.Parameters.AddWithValue("$t", tenantId.ToString("D"));
        cmd2.Parameters.AddWithValue("$b", branchId.ToString("D"));
        cmd2.Parameters.AddWithValue("$term", terminalId.ToString("D"));
        cmd2.Parameters.AddWithValue("$now", NowMs());
        cmd2.ExecuteNonQuery();

        using var cmd3 = conn.CreateCommand();
        cmd3.CommandText = """
                           INSERT OR IGNORE INTO hash_checkpoint(tenant_id, branch_id, terminal_id, base_seq, inventory_hash, products_hash, leases_hash, snapshot_hash, updated_at_ms)
                           VALUES ($t, $b, $term, 0, '', '', '', '', $now);
                           """;
        cmd3.Parameters.AddWithValue("$t", tenantId.ToString("D"));
        cmd3.Parameters.AddWithValue("$b", branchId.ToString("D"));
        cmd3.Parameters.AddWithValue("$term", terminalId.ToString("D"));
        cmd3.Parameters.AddWithValue("$now", NowMs());
        cmd3.ExecuteNonQuery();
    }

    public void SetHealth(Guid tenantId, Guid branchId, Guid terminalId, string state, string? lastError = null, object? meta = null)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          UPDATE terminal_health
                             SET state=$st,
                                 updated_at_ms=$now,
                                 last_applied_seq=(SELECT last_applied_seq FROM sync_checkpoint WHERE tenant_id=$t AND branch_id=$b AND terminal_id=$term),
                                 last_error=$err,
                                 meta_json=$meta
                           WHERE tenant_id=$t AND branch_id=$b AND terminal_id=$term;
                          """;
        cmd.Parameters.AddWithValue("$st", state);
        cmd.Parameters.AddWithValue("$now", NowMs());
        cmd.Parameters.AddWithValue("$err", (object?)lastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$meta", meta == null ? (object)DBNull.Value : JsonSerializer.Serialize(meta));
        cmd.Parameters.AddWithValue("$t", tenantId.ToString("D"));
        cmd.Parameters.AddWithValue("$b", branchId.ToString("D"));
        cmd.Parameters.AddWithValue("$term", terminalId.ToString("D"));
        cmd.ExecuteNonQuery();
    }

    public long GetLastAppliedSeq(Guid tenantId, Guid branchId, Guid terminalId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          SELECT last_applied_seq
                            FROM sync_checkpoint
                           WHERE tenant_id=$t AND branch_id=$b AND terminal_id=$term;
                          """;
        cmd.Parameters.AddWithValue("$t", tenantId.ToString("D"));
        cmd.Parameters.AddWithValue("$b", branchId.ToString("D"));
        cmd.Parameters.AddWithValue("$term", terminalId.ToString("D"));
        var o = cmd.ExecuteScalar();
        return o == null ? 0 : Convert.ToInt64(o);
    }

    public void SetLastAppliedSeq(Guid tenantId, Guid branchId, Guid terminalId, long seq)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          UPDATE sync_checkpoint
                             SET last_applied_seq=$seq,
                                 updated_at_ms=$now
                           WHERE tenant_id=$t AND branch_id=$b AND terminal_id=$term;
                          """;
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$now", NowMs());
        cmd.Parameters.AddWithValue("$t", tenantId.ToString("D"));
        cmd.Parameters.AddWithValue("$b", branchId.ToString("D"));
        cmd.Parameters.AddWithValue("$term", terminalId.ToString("D"));
        cmd.ExecuteNonQuery();
    }

    public void UpsertProduct(ProductCacheRow p)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          INSERT INTO product_cache(product_id, sku, name, price, tax_rate, updated_at_ms)
                          VALUES ($id, $sku, $name, $price, $tax, $now)
                          ON CONFLICT(product_id) DO UPDATE SET
                            sku=excluded.sku,
                            name=excluded.name,
                            price=excluded.price,
                            tax_rate=excluded.tax_rate,
                            updated_at_ms=excluded.updated_at_ms;
                          """;
        cmd.Parameters.AddWithValue("$id", p.ProductId);
        cmd.Parameters.AddWithValue("$sku", p.Sku);
        cmd.Parameters.AddWithValue("$name", p.Name);
        cmd.Parameters.AddWithValue("$price", p.Price);
        cmd.Parameters.AddWithValue("$tax", p.TaxRate);
        cmd.Parameters.AddWithValue("$now", NowMs());
        cmd.ExecuteNonQuery();
    }

    public void UpsertInventory(InventoryCacheRow i)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          INSERT INTO inventory_cache(product_id, on_hand, reserved, updated_at_ms)
                          VALUES ($id, $on, $res, $now)
                          ON CONFLICT(product_id) DO UPDATE SET
                            on_hand=excluded.on_hand,
                            reserved=excluded.reserved,
                            updated_at_ms=excluded.updated_at_ms;
                          """;
        cmd.Parameters.AddWithValue("$id", i.ProductId);
        cmd.Parameters.AddWithValue("$on", i.OnHand);
        cmd.Parameters.AddWithValue("$res", i.Reserved);
        cmd.Parameters.AddWithValue("$now", NowMs());
        cmd.ExecuteNonQuery();
    }

    public void AppendReplayLog(string kind, string message, string? requestId = null, object? meta = null)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                              INSERT INTO replay_log(at_ms, kind, request_id, message, meta_json)
                              VALUES ($at, $kind, $rid, $msg, $meta);
                              """;
            cmd.Parameters.AddWithValue("$at", NowMs());
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$rid", (object?)requestId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$msg", message);
            cmd.Parameters.AddWithValue("$meta", meta == null ? (object)DBNull.Value : JsonSerializer.Serialize(meta));
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // never throw from logging
        }
    }

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string Sha256Hex(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public (long LastAppliedSeq, long? MinPendingSeq, long? GapExpectedSeq) GetGapInfo(Guid tenantId, Guid branchId, Guid terminalId)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        long lastApplied;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                              SELECT last_applied_seq
                                FROM sync_checkpoint
                               WHERE tenant_id=$t AND branch_id=$b AND terminal_id=$term;
                              """;
            cmd.Parameters.AddWithValue("$t", tenantId.ToString("D"));
            cmd.Parameters.AddWithValue("$b", branchId.ToString("D"));
            cmd.Parameters.AddWithValue("$term", terminalId.ToString("D"));
            lastApplied = Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
        }

        long? minPending;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT MIN(seq) FROM pending_events;";
            var o = cmd.ExecuteScalar();
            minPending = (o == null || o is DBNull) ? null : Convert.ToInt64(o);
        }

        tx.Commit();

        var expected = lastApplied + 1;
        if (minPending.HasValue && minPending.Value > expected)
            return (lastApplied, minPending, expected);
        return (lastApplied, minPending, null);
    }

    public (string InventoryHash, string ProductsHash, string LeasesHash, string SnapshotHash, string SnapshotHashNoLeases) ComputeLocalHashes(long snapshotBaseSeqForHash)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        var invText = new StringBuilder();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT product_id, on_hand, reserved FROM inventory_cache ORDER BY product_id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var pid = r.GetString(0);
                var onHand = Math.Round(r.GetDouble(1), 4);
                var reserved = Math.Round(r.GetDouble(2), 4);
                invText.Append(pid).Append('|')
                    .Append(onHand.ToString("0.####", CultureInfo.InvariantCulture)).Append('|')
                    .Append(reserved.ToString("0.####", CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        var prodText = new StringBuilder();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT product_id, sku, name, price, tax_rate FROM product_cache ORDER BY product_id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var pid = r.GetString(0);
                var sku = r.GetString(1);
                var name = r.GetString(2);
                var price = Math.Round(r.GetDouble(3), 4);
                var tax = Math.Round(r.GetDouble(4), 4);
                prodText.Append(pid).Append('|')
                    .Append(sku).Append('|')
                    .Append(name).Append('|')
                    .Append(price.ToString("0.####", CultureInfo.InvariantCulture)).Append('|')
                    .Append(tax.ToString("0.####", CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        var leaseText = new StringBuilder();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                              SELECT lease_id, terminal_id, status, expires_at_ms
                                FROM leases
                               WHERE status='active'
                               ORDER BY lease_id;
                              """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var lid = r.GetString(0);
                var termId = r.GetString(1);
                var status = r.GetString(2);
                var exp = r.GetInt64(3);
                leaseText.Append(lid).Append('|')
                    .Append(termId).Append('|')
                    .Append(status).Append('|')
                    .Append(exp).Append('\n');

                using var cmdLines = conn.CreateCommand();
                cmdLines.Transaction = tx;
                cmdLines.CommandText = """
                                       SELECT product_id, qty_allocated, qty_used
                                         FROM lease_lines
                                        WHERE lease_id=$lid
                                        ORDER BY product_id;
                                       """;
                cmdLines.Parameters.AddWithValue("$lid", lid);
                using var rl = cmdLines.ExecuteReader();
                while (rl.Read())
                {
                    var pid = rl.GetString(0);
                    var alloc = Math.Round(rl.GetDouble(1), 4);
                    var used = Math.Round(rl.GetDouble(2), 4);
                    leaseText.Append("  ").Append(pid).Append('|')
                        .Append(alloc.ToString("0.####", CultureInfo.InvariantCulture)).Append('|')
                        .Append(used.ToString("0.####", CultureInfo.InvariantCulture)).Append('\n');
                }
            }
        }

        tx.Commit();

        var invHash = Sha256Hex(invText.ToString());
        var prodHash = Sha256Hex(prodText.ToString());
        var leasesHash = Sha256Hex(leaseText.ToString());
        var snapshotHashNoLeases = Sha256Hex($"{snapshotBaseSeqForHash}|{invHash}|{prodHash}");
        var snapshotHash = Sha256Hex($"{snapshotBaseSeqForHash}|{invHash}|{prodHash}|{leasesHash}");

        // Persist last computed hashes for observability/diagnostics (best-effort).
        try
        {
            using var conn2 = OpenConnection();
            using var cmd = conn2.CreateCommand();
            cmd.CommandText = """
                              UPDATE hash_checkpoint
                                 SET base_seq=$seq,
                                     inventory_hash=$ih,
                                     products_hash=$ph,
                                     leases_hash=$lh,
                                     snapshot_hash=$sh,
                                     updated_at_ms=$now
                               WHERE tenant_id=(SELECT tenant_id FROM sync_checkpoint LIMIT 1)
                                 AND branch_id=(SELECT branch_id FROM sync_checkpoint LIMIT 1)
                                 AND terminal_id=(SELECT terminal_id FROM sync_checkpoint LIMIT 1);
                              """;
            cmd.Parameters.AddWithValue("$seq", snapshotBaseSeqForHash);
            cmd.Parameters.AddWithValue("$ih", invHash);
            cmd.Parameters.AddWithValue("$ph", prodHash);
            cmd.Parameters.AddWithValue("$lh", leasesHash);
            cmd.Parameters.AddWithValue("$sh", snapshotHash);
            cmd.Parameters.AddWithValue("$now", NowMs());
            cmd.ExecuteNonQuery();
        }
        catch { }

        return (invHash, prodHash, leasesHash, snapshotHash, snapshotHashNoLeases);
    }

    public void ApplySnapshotAtomic(Guid tenantId, Guid branchId, Guid terminalId, JsonElement snapshot)
    {
        // Snapshot shape: SnapshotGetResult from API
        if (!snapshot.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("snapshot not ok");

        var baseSeq = snapshot.GetProperty("baseSeq").GetInt64();

        if (!snapshot.TryGetProperty("products", out var prodEl) || prodEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("snapshot missing products");
        if (!snapshot.TryGetProperty("inventory", out var invEl) || invEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("snapshot missing inventory");

        var now = NowMs();

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        // Temp staging tables inside same transaction.
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                              CREATE TEMP TABLE IF NOT EXISTS product_cache_new (
                                product_id TEXT PRIMARY KEY,
                                sku TEXT NOT NULL,
                                name TEXT NOT NULL,
                                price REAL NOT NULL,
                                tax_rate REAL NOT NULL,
                                updated_at_ms INTEGER NOT NULL
                              );
                              CREATE TEMP TABLE IF NOT EXISTS inventory_cache_new (
                                product_id TEXT PRIMARY KEY,
                                on_hand REAL NOT NULL,
                                reserved REAL NOT NULL,
                                updated_at_ms INTEGER NOT NULL
                              );
                              DELETE FROM product_cache_new;
                              DELETE FROM inventory_cache_new;
                              """;
            cmd.ExecuteNonQuery();
        }

        // Test hook: allow simulating crash mid-snapshot after staging tables are created.
        // Used only by integration tests via environment variable.
        if (int.TryParse(Environment.GetEnvironmentVariable("POSEDGE_TEST_SNAPSHOT_FAILFAST_STAGE"), out var stage) && stage == 1)
            Environment.FailFast("POSEDGE_TEST_SNAPSHOT_FAILFAST_STAGE=1");

        // Stage products
        foreach (var p in prodEl.EnumerateArray())
        {
            var id = p.GetProperty("productId").GetGuid().ToString("D");
            var sku = p.GetProperty("sku").GetString() ?? "";
            var name = p.GetProperty("name").GetString() ?? "";
            var price = p.GetProperty("price").GetDecimal();
            var tax = p.GetProperty("taxRate").GetDecimal();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                              INSERT INTO product_cache_new(product_id, sku, name, price, tax_rate, updated_at_ms)
                              VALUES ($id, $sku, $name, $price, $tax, $now);
                              """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$sku", sku);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$price", (double)price);
            cmd.Parameters.AddWithValue("$tax", (double)tax);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("POSEDGE_TEST_SNAPSHOT_FAILFAST_STAGE"), out stage) && stage == 2)
            Environment.FailFast("POSEDGE_TEST_SNAPSHOT_FAILFAST_STAGE=2");

        // Stage inventory
        foreach (var i in invEl.EnumerateArray())
        {
            var id = i.GetProperty("productId").GetGuid().ToString("D");
            var onHand = i.GetProperty("onHand").GetDecimal();
            var reserved = i.GetProperty("reserved").GetDecimal();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                              INSERT INTO inventory_cache_new(product_id, on_hand, reserved, updated_at_ms)
                              VALUES ($id, $on, $res, $now);
                              """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$on", (double)onHand);
            cmd.Parameters.AddWithValue("$res", (double)reserved);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("POSEDGE_TEST_SNAPSHOT_FAILFAST_STAGE"), out stage) && stage == 3)
            Environment.FailFast("POSEDGE_TEST_SNAPSHOT_FAILFAST_STAGE=3");

        // Optional leases snapshot (active only)
        if (snapshot.TryGetProperty("leases", out var leasesEl) && leasesEl.ValueKind == JsonValueKind.Array)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                                  DELETE FROM lease_lines;
                                  DELETE FROM leases;
                                  """;
                cmd.ExecuteNonQuery();
            }

            foreach (var l in leasesEl.EnumerateArray())
            {
                var leaseId = l.GetProperty("leaseId").GetGuid().ToString("D");
                var termId = l.GetProperty("terminalId").GetGuid().ToString("D");
                var status = l.GetProperty("status").GetString() ?? "active";
                var expiresAt = l.GetProperty("expiresAt").GetDateTimeOffset().ToUnixTimeMilliseconds();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                                      INSERT INTO leases(lease_id, tenant_id, branch_id, terminal_id, created_at_ms, expires_at_ms, status)
                                      VALUES ($lid, $t, $b, $term, $now, $exp, $st);
                                      """;
                    cmd.Parameters.AddWithValue("$lid", leaseId);
                    cmd.Parameters.AddWithValue("$t", tenantId.ToString("D"));
                    cmd.Parameters.AddWithValue("$b", branchId.ToString("D"));
                    cmd.Parameters.AddWithValue("$term", termId);
                    cmd.Parameters.AddWithValue("$now", now);
                    cmd.Parameters.AddWithValue("$exp", expiresAt);
                    cmd.Parameters.AddWithValue("$st", status);
                    cmd.ExecuteNonQuery();
                }

                if (l.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ln in lines.EnumerateArray())
                    {
                        var pid = ln.GetProperty("productId").GetGuid().ToString("D");
                        var alloc = ln.GetProperty("qtyAllocated").GetDecimal();
                        var used = ln.GetProperty("qtyUsed").GetDecimal();
                        using var cmd = conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = """
                                          INSERT INTO lease_lines(lease_id, product_id, qty_allocated, qty_used)
                                          VALUES ($lid, $pid, $a, $u);
                                          """;
                        cmd.Parameters.AddWithValue("$lid", leaseId);
                        cmd.Parameters.AddWithValue("$pid", pid);
                        cmd.Parameters.AddWithValue("$a", (double)alloc);
                        cmd.Parameters.AddWithValue("$u", (double)used);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        // Replace caches atomically (swap via delete+insert inside tx).
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                              DELETE FROM product_cache;
                              INSERT INTO product_cache(product_id, sku, name, price, tax_rate, updated_at_ms)
                              SELECT product_id, sku, name, price, tax_rate, updated_at_ms FROM product_cache_new;

                              DELETE FROM inventory_cache;
                              INSERT INTO inventory_cache(product_id, on_hand, reserved, updated_at_ms)
                              SELECT product_id, on_hand, reserved, updated_at_ms FROM inventory_cache_new;
                              """;
            cmd.ExecuteNonQuery();
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("POSEDGE_TEST_SNAPSHOT_FAILFAST_STAGE"), out stage) && stage == 4)
            Environment.FailFast("POSEDGE_TEST_SNAPSHOT_FAILFAST_STAGE=4");

        // Reset event stream state to baseSeq.
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                              DELETE FROM pending_events;
                              DELETE FROM applied_events;
                              UPDATE sync_checkpoint
                                 SET last_applied_seq=$seq,
                                     updated_at_ms=$now
                               WHERE tenant_id=$t AND branch_id=$b AND terminal_id=$term;
                              """;
            cmd.Parameters.AddWithValue("$seq", baseSeq);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$t", tenantId.ToString("D"));
            cmd.Parameters.AddWithValue("$b", branchId.ToString("D"));
            cmd.Parameters.AddWithValue("$term", terminalId.ToString("D"));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }
}

public sealed record ProductCacheRow(string ProductId, string Sku, string Name, double Price, double TaxRate);
public sealed record InventoryCacheRow(string ProductId, double OnHand, double Reserved);

