using Grunflex.Licensing.Security;
using Microsoft.Data.Sqlite;

if (args.Length < 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Uso: SeedAdmin <ruta-grunflex.db> [usuario] [password] [nombre]");
    return 2;
}

var db = args[0];
var user = args.Length > 1 ? args[1] : "claudio";
var pass = args.Length > 2 ? args[2] : "demo1234";
var nombre = args.Length > 3 ? args[3] : "Administrador";

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

await using (var exists = c.CreateCommand())
{
    exists.CommandText = "SELECT COUNT(*) FROM Usuarios WHERE Username = $u";
    exists.Parameters.AddWithValue("$u", user);
    if (Convert.ToInt32(await exists.ExecuteScalarAsync()) > 0)
    {
        Console.WriteLine("EXISTS");
        return 0;
    }
}

var id = Guid.NewGuid().ToString("D").ToUpperInvariant();
await using (var ins = c.CreateCommand())
{
    ins.CommandText = "INSERT INTO Usuarios (Id, Nombre, Username, Rol, Password) VALUES ($id, $n, $u, 'Admin', $p)";
    ins.Parameters.AddWithValue("$id", id);
    ins.Parameters.AddWithValue("$n", nombre);
    ins.Parameters.AddWithValue("$u", user);
    ins.Parameters.AddWithValue("$p", PasswordHasher.Hash(pass));
    await ins.ExecuteNonQueryAsync();
}

Console.WriteLine($"CREATED user={user}");
return 0;
