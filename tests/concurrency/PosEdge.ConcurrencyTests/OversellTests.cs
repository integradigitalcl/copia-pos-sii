using Microsoft.EntityFrameworkCore;
using PosEdge.Application.Sales;
using PosEdge.Infrastructure;
using Xunit;

namespace PosEdge.ConcurrencyTests;

public sealed class OversellTests
{
    private const string Cs = "Host=127.0.0.1;Port=5432;Database=posedgedb;Username=posedge;Password=posedge;Include Error Detail=true";

    [Fact]
    public async Task Concurrent_commits_do_not_oversell()
    {
        // Arrange: reset inventory for product1 to 1
        var options = new DbContextOptionsBuilder<PosEdgeDbContext>()
            .UseNpgsql(Cs)
            .Options;

        await using (var db = new PosEdgeDbContext(options))
        {
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE inventory
                   SET on_hand = 1,
                       reserved = 0,
                       updated_at = now()
                 WHERE tenant_id = '11111111-1111-1111-1111-111111111111'
                   AND branch_id = '22222222-2222-2222-2222-222222222222'
                   AND product_id = '33333333-3333-3333-3333-333333333333';
                """);
        }

        var cmdA = new SaleCommitCommand(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            "TEST-A",
            "MXN",
            new[]
            {
                new SaleCommitLine(
                    Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    "SKU-COCA-600",
                    "Refresco 600ml",
                    1m,
                    18.50m,
                    0.16m)
            },
            new[]
            {
                new SaleCommitPayment("cash", 20m, "MXN", null)
            },
            0m);

        var cmdB = cmdA with { RequestId = "TEST-B", TerminalId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), CashSessionId = Guid.Parse("66666666-6666-6666-6666-666666666666") };

        async Task<SaleCommitResult> Run(string reqId, SaleCommitCommand cmd)
        {
            var o = new DbContextOptionsBuilder<PosEdgeDbContext>().UseNpgsql(Cs).Options;
            await using var db = new PosEdgeDbContext(o);
            var h = new SaleCommitHandler(db);
            return await h.Handle(cmd, CancellationToken.None);
        }

        // Act
        var t1 = Run("A", cmdA);
        var t2 = Run("B", cmdB);
        var results = await Task.WhenAll(t1, t2);

        // Assert: exactly one OK, one INSUFFICIENT_STOCK
        var ok = results.Count(r => r.Ok);
        Assert.Equal(1, ok);
        Assert.Contains(results, r => !r.Ok && r.Code == "INSUFFICIENT_STOCK");

        await using (var db = new PosEdgeDbContext(options))
        {
            var onHand = await db.Inventory.AsNoTracking()
                .Where(i => i.TenantId == Guid.Parse("11111111-1111-1111-1111-111111111111")
                            && i.BranchId == Guid.Parse("22222222-2222-2222-2222-222222222222")
                            && i.ProductId == Guid.Parse("33333333-3333-3333-3333-333333333333"))
                .Select(i => i.OnHand)
                .SingleAsync();
            Assert.Equal(0m, onHand);
        }
    }

    [Fact]
    public async Task Same_requestId_is_idempotent()
    {
        var options = new DbContextOptionsBuilder<PosEdgeDbContext>()
            .UseNpgsql(Cs)
            .Options;
        var reqId = "TEST-IDEMPOTENT-1";
        var cmd = new SaleCommitCommand(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            reqId,
            "MXN",
            new[]
            {
                new SaleCommitLine(
                    Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    "SKU-PAN-001",
                    "Pan dulce",
                    1m,
                    12.00m,
                    0.00m)
            },
            new[] { new SaleCommitPayment("cash", 12m, "MXN", null) },
            0m);

        await using var db1 = new PosEdgeDbContext(options);
        var h1 = new SaleCommitHandler(db1);
        var r1 = await h1.Handle(cmd, CancellationToken.None);
        Assert.True(r1.Ok);
        Assert.NotNull(r1.SaleId);

        await using var db2 = new PosEdgeDbContext(options);
        var h2 = new SaleCommitHandler(db2);
        var r2 = await h2.Handle(cmd, CancellationToken.None);
        Assert.True(r2.Ok);
        Assert.Equal(r1.SaleId, r2.SaleId);
        Assert.Equal(r1.ServerTicket, r2.ServerTicket);
    }
}

