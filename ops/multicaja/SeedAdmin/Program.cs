using System.Security.Cryptography;
using System.Text;
using Grunflex.Licensing.Security;
using Microsoft.Data.Sqlite;

if (args.Length < 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Uso: SeedAdmin <ruta-grunflex.db> [usuario] [password] [nombre] [--web-db <ruta-grunflex-pos.db>]");
    return 2;
}

var db = args[0];
var user = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : "admin";
var pass = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : "demo1234";
var nombre = args.Length > 3 && !args[3].StartsWith("--", StringComparison.Ordinal) ? args[3] : "Administrador";
string? webDb = null;
for (var i = 1; i < args.Length - 1; i++)
{
    if (string.Equals(args[i], "--web-db", StringComparison.OrdinalIgnoreCase))
        webDb = args[i + 1];
}

await using var c = new SqliteConnection($"Data Source={db};Cache=Shared");
await c.OpenAsync();

await using (var check = c.CreateCommand())
{
    check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Usuarios'";
    if (await check.ExecuteScalarAsync() is not string)
    {
        Console.WriteLine("NO_TABLE");
        return 3;
    }
}

var hash = PasswordHasher.Hash(pass);
var updated = false;
await using (var exists = c.CreateCommand())
{
    exists.CommandText = "SELECT Id FROM Usuarios WHERE lower(Username) = lower($u) LIMIT 1";
    exists.Parameters.AddWithValue("$u", user);
    var existingId = await exists.ExecuteScalarAsync();
    if (existingId is string idText && !string.IsNullOrWhiteSpace(idText))
    {
        await using var upd = c.CreateCommand();
        upd.CommandText = "UPDATE Usuarios SET Password = $p, Nombre = $n, Rol = 'Admin' WHERE Id = $id";
        upd.Parameters.AddWithValue("$p", hash);
        upd.Parameters.AddWithValue("$n", nombre);
        upd.Parameters.AddWithValue("$id", idText);
        await upd.ExecuteNonQueryAsync();
        Console.WriteLine($"UPDATED user={user}");
        updated = true;
    }
}

if (!updated)
{
    var id = Guid.NewGuid().ToString("D").ToUpperInvariant();
    await using var ins = c.CreateCommand();
    ins.CommandText = "INSERT INTO Usuarios (Id, Nombre, Username, Rol, Password) VALUES ($id, $n, $u, 'Admin', $p)";
    ins.Parameters.AddWithValue("$id", id);
    ins.Parameters.AddWithValue("$n", nombre);
    ins.Parameters.AddWithValue("$u", user);
    ins.Parameters.AddWithValue("$p", hash);
    await ins.ExecuteNonQueryAsync();
    Console.WriteLine($"CREATED user={user}");
}

if (!string.IsNullOrWhiteSpace(webDb) && File.Exists(webDb))
{
    var webHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pass)));
    await using var web = new SqliteConnection($"Data Source={webDb};Cache=Shared");
    await web.OpenAsync();
    await using var webCheck = web.CreateCommand();
    webCheck.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Users'";
    if (await webCheck.ExecuteScalarAsync() is string)
    {
        await using var webUpd = web.CreateCommand();
        webUpd.CommandText =
            """
            UPDATE Users
            SET PasswordHash = $p, MustChangePassword = 1, Active = 1, Role = 'Administrador', Permissions = 'all'
            WHERE lower(UserName) = 'admin'
            """;
        webUpd.Parameters.AddWithValue("$p", webHash);
        var rows = await webUpd.ExecuteNonQueryAsync();
        if (rows == 0)
        {
            await using var webIns = web.CreateCommand();
            webIns.CommandText =
                """
                INSERT INTO Users (UserName, PasswordHash, Role, Permissions, Active, MustChangePassword)
                VALUES ('admin', $p, 'Administrador', 'all', 1, 1)
                """;
            webIns.Parameters.AddWithValue("$p", webHash);
            await webIns.ExecuteNonQueryAsync();
        }

        Console.WriteLine("WEB_DB_SYNCED");
    }
}

return 0;
