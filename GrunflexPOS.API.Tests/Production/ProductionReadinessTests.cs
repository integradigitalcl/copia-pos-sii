using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Grunflex.Licensing;
using Grunflex.Licensing.Security;
using GrunflexPOS.API.Commerce;
using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Models;
using GrunflexPOS.API.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrunflexPOS.API.Tests.Production;

public sealed class ProductionReadinessTests : IClassFixture<IntegrationTestFactory>
{
    private readonly IntegrationTestFactory _factory;

    public ProductionReadinessTests(IntegrationTestFactory factory) => _factory = factory;

    [Fact]
    public async Task License_Activation_Returns_GFv2_Token()
    {
        await using var scope = await SeedLicenseAsync(numberOfBoxes: 2);
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("api/licensing/activate", new LicensingActivateRequest
        {
            ActivationId = "TEST-ACT-001",
            MachineName = Environment.MachineName
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<LicensingTokenResponse>();
        body.Should().NotBeNull();
        body!.LicenseToken.Should().StartWith("GFv2.");
        body.NumberOfBoxes.Should().Be(2);
    }

    [Fact]
    public async Task License_Refresh_Renews_Token()
    {
        await using var scope = await SeedLicenseAsync(numberOfBoxes: 1);
        var client = _factory.CreateClient();

        var refresh = await client.PostAsJsonAsync("api/licensing/refresh", new LicensingActivateRequest
        {
            ActivationId = "TEST-ACT-001",
            MachineName = Environment.MachineName
        });

        refresh.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await refresh.Content.ReadFromJsonAsync<LicensingTokenResponse>();
        body!.LicenseToken.Should().StartWith("GFv2.");
    }

    [Fact]
    public async Task License_Expired_Record_Rejected()
    {
        await using var scope = await SeedLicenseAsync(expUtc: DateTime.UtcNow.AddDays(-1));
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("api/licensing/activate", new LicensingActivateRequest
        {
            ActivationId = "TEST-ACT-001",
            MachineName = Environment.MachineName
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task License_Status_Includes_NumberOfBoxes()
    {
        await using var scope = await SeedLicenseAsync(numberOfBoxes: 3);
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("api/licensing/status?activationId=TEST-ACT-001");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<LicensingStatusResponse>();
        body!.Found.Should().BeTrue();
        body.NumberOfBoxes.Should().Be(3);
    }

    [Fact]
    public async Task Multicaja_AutoRegistro_Respects_Box_Limit()
    {
        await using var seed = await SeedCommerceAsync(numberOfBoxes: 1, existingActiveCajas: 1);
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("api/multicaja/cajas/auto-registro", new
        {
            machineName = "PC-EXTRA"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        json.ToLowerInvariant().Should().Contain("límite");
    }

    [Fact]
    public async Task Multicaja_Login_BCrypt_Works()
    {
        await using var seed = await SeedCommerceWithUserAsync();
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("api/multicaja/login", new
        {
            username = seed.Username,
            password = "1234"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Multicaja_Venta_Commit_Persists_Sale()
    {
        await using var seed = await SeedCommerceWithUserAsync(openSession: true, stock: 10);
        var client = _factory.CreateClient();

        var commit = await client.PostAsJsonAsync("api/multicaja/ventas/commit", new
        {
            requestId = Guid.NewGuid().ToString("N"),
            cajaId = seed.CajaId,
            cajaSesionId = seed.SesionId,
            usuarioId = seed.UserId,
            metodoPago = "Efectivo",
            esConsumoPersonal = false,
            items = new[]
            {
                new { codigoBarras = seed.Barcode, producto = "Pan", cantidad = 2, precio = 100m }
            }
        });

        commit.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await commit.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("ok").GetBoolean().Should().BeTrue();

        await using var checkScope = _factory.Services.CreateAsyncScope();
        var pos = checkScope.ServiceProvider.GetRequiredService<PosCommerceDbContext>();
        (await pos.Ventas.CountAsync()).Should().BeGreaterThan(0);
    }

    [Fact]
    public void PasswordHasher_BCrypt_Upgrade_Legacy_PlainText()
    {
        const string plain = "admin123";
        var hash = PasswordHasher.Hash(plain);
        PasswordHasher.TryVerifyAndUpgrade(plain, hash, out var upgraded).Should().BeTrue();
        upgraded.Should().BeNull();
        PasswordHasher.TryVerifyAndUpgrade("wrong", hash, out _).Should().BeFalse();
    }

    private async Task<AsyncServiceScope> SeedLicenseAsync(
        int numberOfBoxes = 1,
        DateTime? expUtc = null)
    {
        await _factory.EnsureHostReadyAsync();
        var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        db.LicenseIssuerRecords.Add(new LicenseIssuerRecord
        {
            ActivationId = "TEST-ACT-001",
            CustomerName = "Test",
            BusinessName = "Minimarket",
            ExpUtc = expUtc ?? DateTime.UtcNow.AddDays(30),
            Multicaja = true,
            OnlineSupport = true,
            CloudBackup = true,
            PrioritySupport = false,
            NumberOfBoxes = numberOfBoxes,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return scope;
    }

    private async Task<CommerceSeed> SeedCommerceAsync(int numberOfBoxes, int existingActiveCajas)
    {
        var scope = await SeedLicenseAsync(numberOfBoxes);
        var pos = scope.ServiceProvider.GetRequiredService<PosCommerceDbContext>();
        var empresa = new CommerceEmpresa { Id = Guid.NewGuid(), Nombre = "Test", FechaCreacion = DateTime.UtcNow };
        pos.Empresas.Add(empresa);
        for (var i = 0; i < existingActiveCajas; i++)
        {
            pos.Cajas.Add(new CommerceCaja
            {
                Id = Guid.NewGuid(),
                Nombre = $"Caja {i + 1}",
                EmpresaId = empresa.Id,
                Activa = true,
                FechaCreacion = DateTime.UtcNow
            });
        }

        await pos.SaveChangesAsync();
        return new CommerceSeed(scope, Guid.Empty, Guid.Empty, Guid.Empty, string.Empty, string.Empty);
    }

    private async Task<CommerceSeed> SeedCommerceWithUserAsync(bool openSession = false, int stock = 0)
    {
        var scope = await SeedLicenseAsync(numberOfBoxes: 5);
        var pos = scope.ServiceProvider.GetRequiredService<PosCommerceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var empresaId = Guid.NewGuid();
        var cajaId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var username = $"cajero_{suffix}";
        var barcode = $"750{suffix}0";
        pos.Empresas.Add(new CommerceEmpresa { Id = empresaId, Nombre = "Test", FechaCreacion = DateTime.UtcNow });
        pos.Cajas.Add(new CommerceCaja
        {
            Id = cajaId,
            Nombre = "Caja 1",
            EmpresaId = empresaId,
            Activa = true,
            FechaCreacion = DateTime.UtcNow
        });
        pos.Usuarios.Add(new CommerceUsuario
        {
            Id = userId,
            Username = username,
            Nombre = "Cajero",
            Rol = "Cajero",
            Password = PasswordHasher.Hash("1234")
        });
        pos.Productos.Add(new CommerceProducto
        {
            Nombre = "Pan",
            CodigoBarras = barcode,
            Precio = 100,
            Stock = stock
        });

        Guid sesionId = Guid.Empty;
        if (openSession)
        {
            sesionId = Guid.NewGuid();
            pos.CajaSesiones.Add(new CommerceCajaSesion
            {
                Id = sesionId,
                CajaId = cajaId,
                UsuarioAperturaId = userId,
                Cajero = username,
                Abierta = true,
                FechaApertura = DateTime.UtcNow,
                TotalVentas = 0
            });
        }

        await pos.SaveChangesAsync();
        return new CommerceSeed(scope, cajaId, sesionId, userId, username, barcode);
    }

    private sealed record CommerceSeed(
        AsyncServiceScope Scope,
        Guid CajaId,
        Guid SesionId,
        Guid UserId,
        string Username,
        string Barcode) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Scope.DisposeAsync();
    }
}
