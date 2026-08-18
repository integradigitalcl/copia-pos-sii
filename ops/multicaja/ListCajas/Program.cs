using Microsoft.Data.Sqlite;
if (args.Length < 1) return 1;
await using var c = new SqliteConnection($"Data Source={args[0]};Cache=Shared");
await c.OpenAsync();
await using var cmd = c.CreateCommand();
cmd.CommandText = "SELECT Id, Nombre FROM Cajas LIMIT 5";
await using var r = await cmd.ExecuteReaderAsync();
while (await r.ReadAsync()) Console.WriteLine($"{r.GetString(0)}|{r.GetString(1)}");
return 0;
