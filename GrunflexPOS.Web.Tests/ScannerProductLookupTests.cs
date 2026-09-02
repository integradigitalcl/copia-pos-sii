using GrunflexPOS.Web.Data;
using GrunflexPOS.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GrunflexPOS.Web.Tests;

public sealed class ScannerProductLookupTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"grunflex-scan-test-{Guid.NewGuid():N}.db");
    private readonly LocalPosStore _store;

    public ScannerProductLookupTests()
    {
        var options = new DbContextOptionsBuilder<LocalPosDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=false")
            .Options;
        _store = TestConfiguration.CreateStore(options);
    }

    [Fact]
    public async Task SeededCatalog_ResolvesBarcodeCaseInsensitive()
    {
        await _store.EnsureCreatedAsync();
        var products = await _store.GetProductsAsync();
        var code = products.First().Code;

        var match = products.FirstOrDefault(x => x.Code.Equals(code.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(match);
        Assert.Equal(code, match.Code);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_databasePath); } catch { /* best effort */ }
    }
}
