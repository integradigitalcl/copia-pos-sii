using Microsoft.Data.Sqlite;

if (args.Length < 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Uso: AmpliarLicenciaCajas <grunflex_api.db> [cajas=5] [activationId]");
    return 2;
}

var dbPath = args[0];
var boxes = args.Length > 1 && int.TryParse(args[1], out var n) ? Math.Max(2, n) : 5;
var activationId = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2])
    ? args[2].Trim()
    : "GF-20260524-6036";

await using var c = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
await c.OpenAsync();

await using (var table = c.CreateCommand())
{
    table.CommandText = """
        SELECT name FROM sqlite_master
        WHERE type='table' AND name='LicenseIssuerRecords'
        """;
    if (await table.ExecuteScalarAsync() is not string)
    {
        Console.Error.WriteLine("FAIL: tabla LicenseIssuerRecords no existe en " + dbPath);
        return 3;
    }
}

int existingCount;
await using (var cnt = c.CreateCommand())
{
    cnt.CommandText = "SELECT COUNT(*) FROM LicenseIssuerRecords WHERE ActivationId = $aid";
    cnt.Parameters.AddWithValue("$aid", activationId);
    existingCount = Convert.ToInt32(await cnt.ExecuteScalarAsync());
}

var expUtc = DateTime.UtcNow.AddYears(2).ToString("o");
if (existingCount == 0)
{
    await using var ins = c.CreateCommand();
    ins.CommandText = """
        INSERT INTO LicenseIssuerRecords (
            Id, ActivationId, CustomerName, BusinessName, LicenseType,
            NumberOfBoxes, ExpUtc, Multicaja, OnlineSupport, CloudBackup,
            PrioritySupport, LicenseToken, CreatedAtUtc)
        VALUES (
            $id, $aid, $customer, $business, 'Suscripción',
            $boxes, $exp, 1, 1, 1, 0, $token, $now)
        """;
    ins.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
    ins.Parameters.AddWithValue("$aid", activationId);
    ins.Parameters.AddWithValue("$customer", "Cliente multicaja");
    ins.Parameters.AddWithValue("$business", "Grunflex POS");
    ins.Parameters.AddWithValue("$boxes", boxes);
    ins.Parameters.AddWithValue("$exp", expUtc);
    ins.Parameters.AddWithValue("$token", "local-multicaja");
    ins.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
    await ins.ExecuteNonQueryAsync();
    Console.WriteLine($"OK: licencia creada ActivationId={activationId} NumberOfBoxes={boxes}");
}
else
{
    await using var upd = c.CreateCommand();
    upd.CommandText = """
        UPDATE LicenseIssuerRecords
        SET NumberOfBoxes = $boxes, Multicaja = 1
        WHERE ActivationId = $aid
        """;
    upd.Parameters.AddWithValue("$boxes", boxes);
    upd.Parameters.AddWithValue("$aid", activationId);
    var rows = await upd.ExecuteNonQueryAsync();
    Console.WriteLine($"OK: licencia actualizada ({rows} fila) → NumberOfBoxes={boxes}");
}

await using (var read = c.CreateCommand())
{
    read.CommandText = """
        SELECT ActivationId, NumberOfBoxes, Multicaja, ExpUtc
        FROM LicenseIssuerRecords
        ORDER BY CreatedAtUtc DESC
        LIMIT 5
        """;
    await using var r = await read.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        Console.WriteLine(
            $"  {r.GetString(0)} | cajas={r.GetInt32(1)} | multicaja={r.GetInt32(2)} | exp={r.GetString(3)}");
    }
}

return 0;
