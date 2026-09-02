using System.Security.Cryptography;
using System.Text;

namespace GrunflexPOS.Web.Services;

public static class WebAuthPolicy
{
    public const string AllowDemoCredentialsKey = "Security:AllowDemoCredentials";
    public const string InitialCredentialsFileName = "initial-admin-credentials.txt";
    /// <summary>Contraseña inicial fija del instalador (usuario admin).</summary>
    public const string DefaultInitialPassword = "12345678";
    public const string DefaultInitialUserName = "admin";

    public static bool AllowDemoCredentials(IConfiguration configuration) =>
        configuration.GetValue(AllowDemoCredentialsKey, false);

    public static string InitialCredentialsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GrunflexPOS",
            InitialCredentialsFileName);

    public static string SharedCredentialsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GrunflexPOS",
            "config",
            InitialCredentialsFileName);

    public static string? TryReadInitialPassword()
    {
        foreach (var path in new[] { SharedCredentialsPath, InitialCredentialsPath })
        {
            if (!File.Exists(path))
                continue;
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (!trimmed.Contains(':', StringComparison.Ordinal))
                    continue;
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

    public static string GenerateSecurePassword(int length = 16)
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars);
    }

    public static bool IsDemoPasswordHash(string hash) =>
        hash == HashPassword("admin") || hash == HashPassword("cajero");

    public static string HashPassword(string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

    public static bool VerifyPassword(string password, string hash) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(hash),
            SHA256.HashData(Encoding.UTF8.GetBytes(password)));

    public static bool HasInitialCredentialsFile() =>
        File.Exists(SharedCredentialsPath) || File.Exists(InitialCredentialsPath);

    public static void ClearInitialCredentialsFiles()
    {
        foreach (var path in new[] { SharedCredentialsPath, InitialCredentialsPath })
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch { /* best effort */ }
            }
        }
    }

    public static void WriteInitialCredentials(string userName, string password)
    {
        var dir = Path.GetDirectoryName(InitialCredentialsPath)!;
        Directory.CreateDirectory(dir);
        var content =
            $"""
             Grunflex POS Web — credenciales iniciales
             Generado: {DateTime.Now:yyyy-MM-dd HH:mm}
             Equipo: {Environment.MachineName}

             Usuario: {userName}
             Contraseña: {password}

             Cambie esta contraseña en el primer ingreso.
             Elimine este archivo después de anotar las credenciales.
             """;
        File.WriteAllText(InitialCredentialsPath, content);
        var sharedDir = Path.GetDirectoryName(SharedCredentialsPath)!;
        Directory.CreateDirectory(sharedDir);
        File.WriteAllText(SharedCredentialsPath, content.Replace("POS Web", "POS", StringComparison.Ordinal));
    }
}
