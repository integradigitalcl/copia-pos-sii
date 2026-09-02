using Microsoft.Data.Sqlite;

if (args.Length < 2)
{
    Console.Error.WriteLine("Uso: SyncMulticajaSettings <grunflex-pos.db> <apiBaseUrl> [server|client]");
    return 2;
}

var dbPath = args[0];
var apiUrl = args[1].TrimEnd('/') + "/";
var role = args.Length > 2 ? args[2] : "client";

if (!File.Exists(dbPath))
{
    var dbDir = Path.GetDirectoryName(dbPath);
    if (!string.IsNullOrWhiteSpace(dbDir))
        Directory.CreateDirectory(dbDir);
}

await using var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
await conn.OpenAsync();

await using (var ensure = conn.CreateCommand())
{
    ensure.CommandText =
        """
        CREATE TABLE IF NOT EXISTS "Settings" (
            "Key" TEXT NOT NULL CONSTRAINT "PK_Settings" PRIMARY KEY,
            "Value" TEXT NOT NULL,
            "UpdatedAtUtc" TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """;
    await ensure.ExecuteNonQueryAsync();
}

async Task Upsert(string key, string value)
{
    var utc = DateTime.UtcNow.ToString("o");
    await using var cmd = conn.CreateCommand();
    cmd.CommandText =
        """
        INSERT INTO Settings (Key, Value, UpdatedAtUtc) VALUES ($k, $v, $utc)
        ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value, UpdatedAtUtc = excluded.UpdatedAtUtc;
        """;
    cmd.Parameters.AddWithValue("$k", key);
    cmd.Parameters.AddWithValue("$v", value);
    cmd.Parameters.AddWithValue("$utc", utc);
    await cmd.ExecuteNonQueryAsync();
}

await Upsert("multicaja_habilitada", "true");
await Upsert("multicaja_api_url", apiUrl);
await Upsert("terminal_role", role);
Console.WriteLine($"SYNCED role={role} api={apiUrl}");
return 0;
