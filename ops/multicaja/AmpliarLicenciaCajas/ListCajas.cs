using Microsoft.Data.Sqlite;

var db = args[0];
await using var c = new SqliteConnection($"Data Source={db};Cache=Shared");
await c.OpenAsync();
await using var cmd = c.CreateCommand();
cmd.CommandText = "SELECT Id, Nombre FROM Cajas LIMIT 5";
await using var r = await cmd.ExecuteReaderAsync();
while (await r.ReadAsync())
    Console.WriteLine($"{r.GetString(0)} | {r.GetString(1)}");
