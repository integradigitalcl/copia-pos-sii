using PosEdge.Terminal.Replica;
using Xunit;
using System.Text.Json;

namespace PosEdge.LeaseOfflineTests;

public sealed class SnapshotApplyTests
{
    [Fact]
    public void Snapshot_apply_is_transactional_and_keeps_old_cache_on_failure()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "posedgetest-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var db = new TerminalReplicaDb(tmp);
        var tenant = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var term = Guid.NewGuid();
        db.EnsureCheckpoint(tenant, branch, term);

        // Seed old cache
        db.UpsertProduct(new ProductCacheRow("p1", "SKU1", "Name1", 10, 0.16));
        db.UpsertInventory(new InventoryCacheRow("p1", 5, 0));

        // Build a malformed snapshot (missing inventory) to force failure mid-apply.
        var bad = JsonSerializer.Deserialize<JsonElement>("""
        {
          "ok": true,
          "code": "OK",
          "baseSeq": 123,
          "generatedAt": "2020-01-01T00:00:00Z",
          "inventoryHash": "x",
          "productsHash": "y",
          "leasesHash": "z",
          "snapshotHash": "h",
          "products": [
            { "productId": "11111111-1111-1111-1111-111111111111", "sku": "S", "name":"N", "price": 1.0, "taxRate": 0.0 }
          ]
        }
        """);

        Assert.Throws<InvalidOperationException>(() =>
            db.ApplySnapshotAtomic(tenant, branch, term, bad));

        // Old cache should remain.
        var hashes = db.ComputeLocalHashes(snapshotBaseSeqForHash: db.GetLastAppliedSeq(tenant, branch, term));
        Assert.NotEqual("", hashes.InventoryHash);
    }
}

