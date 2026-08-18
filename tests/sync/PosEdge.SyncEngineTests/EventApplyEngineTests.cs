using PosEdge.Terminal.Replica;
using Xunit;

namespace PosEdge.SyncEngineTests;

public sealed class EventApplyEngineTests
{
    [Fact]
    public void Applies_in_order_and_detects_gaps_and_dedups()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "posedgetest-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var db = new TerminalReplicaDb(tmp);
        var tenant = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var term = Guid.NewGuid();
        var engine = new EventApplyEngine(db, tenant, branch, term);

        // out of order: seq 2 arrives first -> pending, gap expected=1
        var r2 = engine.IngestEvent(2, "Sale.Committed", DateTimeOffset.UtcNow, """{"type":"Sale.Committed","seq":2}""");
        Assert.Equal("gap", r2.Kind);
        Assert.Equal(0, r2.LastAppliedSeq);
        Assert.Equal(1, r2.GapExpectedSeq);

        // seq 1 arrives -> drains 1 then 2
        var r1 = engine.IngestEvent(1, "Sale.Committed", DateTimeOffset.UtcNow, """{"type":"Sale.Committed","seq":1}""");
        Assert.Equal("applied", r1.Kind);
        Assert.Equal(2, r1.LastAppliedSeq);

        // duplicate seq 2 ignored
        var dup = engine.IngestEvent(2, "Sale.Committed", DateTimeOffset.UtcNow, """{"type":"Sale.Committed","seq":2}""");
        Assert.Equal("duplicate", dup.Kind);
    }
}

