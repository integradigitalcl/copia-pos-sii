using BCryptNet = BCrypt.Net.BCrypt;

namespace Grunflex.Licensing.Security;

/// <summary>
/// Hash BCrypt para contraseñas de usuarios POS. Compatible con valores legacy en texto plano.
/// </summary>
public static class PasswordHasher
{
    private const int WorkFactor = 12;

    public static bool IsBcryptHash(string? stored) =>
        !string.IsNullOrEmpty(stored) &&
        (stored.StartsWith("$2a$", StringComparison.Ordinal) ||
         stored.StartsWith("$2b$", StringComparison.Ordinal) ||
         stored.StartsWith("$2y$", StringComparison.Ordinal));

    public static string Hash(string plainPassword)
    {
        if (string.IsNullOrEmpty(plainPassword))
            throw new ArgumentException("La contraseña no puede estar vacía.", nameof(plainPassword));

        return BCryptNet.HashPassword(plainPassword, workFactor: WorkFactor);
    }

    public static bool Verify(string plainPassword, string stored)
    {
        if (string.IsNullOrEmpty(stored))
            return false;

        if (IsBcryptHash(stored))
            return BCryptNet.Verify(plainPassword, stored);

        return string.Equals(plainPassword, stored, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifica credenciales. Si el valor almacenado era texto plano y coincide,
    /// devuelve el hash BCrypt en <paramref name="upgradedHash"/> para persistirlo.
    /// </summary>
    public static bool TryVerifyAndUpgrade(string plainPassword, string stored, out string? upgradedHash)
    {
        upgradedHash = null;
        if (string.IsNullOrEmpty(stored))
            return false;

        if (IsBcryptHash(stored))
            return BCryptNet.Verify(plainPassword, stored);

        if (!string.Equals(plainPassword, stored, StringComparison.Ordinal))
            return false;

        upgradedHash = Hash(plainPassword);
        return true;
    }
}
