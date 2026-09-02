using System.Security.Cryptography;
using System.Text;
using Grunflex.Licensing.Security;
using GrunflexPOS.API.Commerce;
using GrunflexPOS.API.Configuration;
using GrunflexPOS.API.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GrunflexPOS.API.Services;

/// <summary>
/// Primera ejecución de la API multicaja: empresa + usuario admin para login POS Web.
/// </summary>
public static class CommerceFirstRunBootstrap
{
    private const string CredentialsFileName = "initial-admin-credentials.txt";

    public static async Task EnsureAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PosCommerceDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(CommerceFirstRunBootstrap));

        if (!await db.Empresas.AnyAsync(ct))
        {
            db.Empresas.Add(new CommerceEmpresa
            {
                Id = Guid.NewGuid(),
                Nombre = "Grunflex",
                FechaCreacion = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Empresa inicial creada para multicaja.");
        }

        if (await db.Usuarios.AnyAsync(ct))
            return;

        var password = TryReadSharedPassword() ?? "12345678";
        db.Usuarios.Add(new CommerceUsuario
        {
            Id = Guid.NewGuid(),
            Username = "admin",
            Nombre = "Administrador",
            Rol = "Admin",
            Password = PasswordHasher.Hash(password)
        });
        await db.SaveChangesAsync(ct);
        WriteSharedCredentials("admin", password);
        logger.LogWarning(
            "Usuario admin inicial creado para multicaja. Credenciales en {Path}",
            GetSharedCredentialsPath());
    }

    public static string GetSharedCredentialsPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GrunflexPOS",
            "config");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, CredentialsFileName);
    }

    public static string? TryReadSharedPassword()
    {
        foreach (var path in new[]
                 {
                     GetSharedCredentialsPath(),
                     Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "GrunflexPOS",
                         CredentialsFileName)
                 })
        {
            if (!File.Exists(path))
                continue;
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Contraseña:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Contrasena:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Password:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = trimmed[(trimmed.IndexOf(':') + 1)..].Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }
        }

        return null;
    }

    private static void WriteSharedCredentials(string userName, string password)
    {
        var content =
            $"""
             Grunflex POS — credenciales iniciales
             Generado: {DateTime.Now:yyyy-MM-dd HH:mm}
             Equipo: {Environment.MachineName}

             Usuario: {userName}
             Contraseña: {password}

             Use estas credenciales para ingresar al POS Web (multicaja).
             Cambie la contraseña en el primer ingreso.
             Elimine este archivo después de anotar las credenciales.
             """;

        var sharedPath = GetSharedCredentialsPath();
        File.WriteAllText(sharedPath, content, Encoding.UTF8);

        var localDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GrunflexPOS");
        Directory.CreateDirectory(localDir);
        File.WriteAllText(Path.Combine(localDir, CredentialsFileName), content, Encoding.UTF8);
    }

    private static string GenerateSecurePassword(int length = 16)
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars);
    }
}
