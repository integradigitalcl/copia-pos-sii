using GrunflexPOS.Web.Data;
using GrunflexPOS.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GrunflexPOS.Web.Tests;

public sealed class WebAuthPolicyTests
{
    [Fact]
    public async Task ProductionSeed_CreatesAdminWithRandomPassword_AndMustChangeFlag()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"grunflex-auth-prod-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<LocalPosDbContext>()
            .UseSqlite($"Data Source={dbPath};Pooling=false")
            .Options;
        var store = TestConfiguration.CreateStore(options, TestConfiguration.ProductionCredentials());

        await store.EnsureCreatedAsync();
        var users = await store.GetUsersAsync();
        var admin = Assert.Single(users);
        Assert.Equal("admin", admin.UserName);
        Assert.True(admin.MustChangePassword);
        Assert.Null(await store.AuthenticateUserAsync("admin", "admin"));
        Assert.True(File.Exists(WebAuthPolicy.InitialCredentialsPath));

        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
        catch
        {
            /* cleanup best effort */
        }
    }

    [Fact]
    public async Task DemoSeed_KeepsKnownUsers_WhenAllowed()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"grunflex-auth-demo-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<LocalPosDbContext>()
            .UseSqlite($"Data Source={dbPath};Pooling=false")
            .Options;
        var store = TestConfiguration.CreateStore(options);

        await store.EnsureCreatedAsync();
        var users = await store.GetUsersAsync();
        Assert.Equal(2, users.Count);
        Assert.Contains(users, u => u.UserName == "admin");
        Assert.Contains(users, u => u.UserName == "cajero");

        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
        catch
        {
            /* cleanup best effort */
        }
    }
}
