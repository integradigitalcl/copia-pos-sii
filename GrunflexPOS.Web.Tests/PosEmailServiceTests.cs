using GrunflexPOS.Web.Data;
using GrunflexPOS.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GrunflexPOS.Web.Tests;

public sealed class PosEmailServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"grunflex-email-test-{Guid.NewGuid():N}.db");
    private readonly LocalPosStore _store;

    public PosEmailServiceTests()
    {
        var options = new DbContextOptionsBuilder<LocalPosDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=false")
            .Options;
        _store = TestConfiguration.CreateStore(options);
    }

    [Fact]
    public async Task LoadSettings_ReturnsNull_WhenDisabled()
    {
        await _store.EnsureCreatedAsync();
        await _store.SetSettingAsync("correo_activo", "false");
        await _store.SetSettingAsync("correo_email", "test@example.com");
        await _store.SetSettingAsync("correo_clave", "secret");

        var service = CreateService();
        var settings = await service.LoadSettingsAsync();

        Assert.Null(settings);
    }

    [Fact]
    public async Task LoadSettings_FallsBackToLegacyWpfKeys()
    {
        await _store.EnsureCreatedAsync();
        await _store.SetSettingsAsync(new Dictionary<string, string>
        {
            ["correo_activo"] = "true",
            ["correo_email"] = "legacy@example.com",
            ["correo_clave"] = "app-password",
            ["correo_host"] = "legacy.smtp.test",
            ["correo_puerto"] = "465",
            ["correo_ssl"] = "false"
        });

        var service = CreateService();
        var settings = await service.LoadSettingsAsync();

        Assert.NotNull(settings);
        Assert.Equal("legacy@example.com", settings.Email);
        Assert.Equal("legacy.smtp.test", settings.Host);
        Assert.Equal(465, settings.Port);
        Assert.False(settings.Ssl);
    }

    [Fact]
    public async Task LoadSettings_PrefersWebKeysOverLegacy()
    {
        await _store.EnsureCreatedAsync();
        await _store.SetSettingsAsync(new Dictionary<string, string>
        {
            ["correo_activo"] = "true",
            ["correo_email"] = "web@example.com",
            ["correo_clave"] = "secret",
            ["correo_smtp_host"] = "smtp.web.test",
            ["correo_smtp_puerto"] = "2525",
            ["correo_smtp_ssl"] = "true",
            ["correo_host"] = "legacy.smtp.test",
            ["correo_puerto"] = "465",
            ["correo_ssl"] = "false"
        });

        var service = CreateService();
        var settings = await service.LoadSettingsAsync();

        Assert.NotNull(settings);
        Assert.Equal("smtp.web.test", settings.Host);
        Assert.Equal(2525, settings.Port);
        Assert.True(settings.Ssl);
    }

    private PosEmailService CreateService()
    {
        var webEnv = new TestWebHostEnvironment();
        var logo = new PosLogoService(_store, webEnv);
        return new PosEmailService(_store, new BoletaPdfService(logo, NullLogger<BoletaPdfService>.Instance),
            NullLogger<PosEmailService>.Instance);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = ".";
        public string WebRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    public void Dispose()
    {
        SqliteConnectionHelper.ClearPools();
        try { File.Delete(_databasePath); } catch { /* best effort */ }
    }
}

internal static class SqliteConnectionHelper
{
    public static void ClearPools() =>
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
}
