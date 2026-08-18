namespace Grunflex.Licensing.Security;

/// <summary>
/// Migración masiva de contraseñas legacy (texto plano) a BCrypt al arranque.
/// </summary>
public static class UserPasswordMigration
{
    /// <summary>
    /// Hashea filas legacy en memoria. El llamador debe invocar SaveChanges si el retorno es &gt; 0.
    /// </summary>
    public static int MigrateLegacyHashes(IReadOnlyList<(string Current, Action<string> SetHash)> rows)
    {
        var migrated = 0;
        foreach (var (current, setHash) in rows)
        {
            if (string.IsNullOrEmpty(current) || PasswordHasher.IsBcryptHash(current))
                continue;

            setHash(PasswordHasher.Hash(current));
            migrated++;
        }

        return migrated;
    }
}
