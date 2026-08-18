using Microsoft.Data.Sqlite;

const string Codigo = "TEST-MC001";
const string Nombre = "Articulo prueba multicaja E2E";
const int Stock = 100;

if (args.Length < 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Uso: SeedProductoPrueba <grunflex.db>");
    return 2;
}

var db = args[0];
await using var c = new SqliteConnection($"Data Source={db};Cache=Shared");
await c.OpenAsync();

int productId;
await using (var find = c.CreateCommand())
{
    find.CommandText = "SELECT Id FROM Productos WHERE CodigoBarras = $c LIMIT 1";
    find.Parameters.AddWithValue("$c", Codigo);
    var found = await find.ExecuteScalarAsync();
    if (found is long l) productId = (int)l;
    else if (found is int i) productId = i;
    else
    {
        await using var ins = c.CreateCommand();
        ins.CommandText = """
            INSERT INTO Productos (
                Nombre, Costo, Precio, Stock, CodigoBarras,
                PrecioMayoreo, InvMinimo, InvMaximo, TipoVenta, Departamento)
            VALUES ($n, 5, 10, $stock, $c, 9, 0, 0, 'pza', 'General')
            """;
        ins.Parameters.AddWithValue("$n", Nombre);
        ins.Parameters.AddWithValue("$stock", Stock);
        ins.Parameters.AddWithValue("$c", Codigo);
        await ins.ExecuteNonQueryAsync();
        await using var pid = c.CreateCommand();
        pid.CommandText = "SELECT last_insert_rowid()";
        productId = Convert.ToInt32(await pid.ExecuteScalarAsync());
    }
}

// Reset stock for repeatable test
await using (var upd = c.CreateCommand())
{
    upd.CommandText = "UPDATE Productos SET Stock = $s, Nombre = $n WHERE Id = $id";
    upd.Parameters.AddWithValue("$s", Stock);
    upd.Parameters.AddWithValue("$n", Nombre);
    upd.Parameters.AddWithValue("$id", productId);
    await upd.ExecuteNonQueryAsync();
}

await using (var ensureInv = c.CreateCommand())
{
    ensureInv.CommandText = """
        CREATE TABLE IF NOT EXISTS "InventoryStocks" (
            "ProductId" INTEGER NOT NULL PRIMARY KEY,
            "QuantityOnHand" INTEGER NOT NULL DEFAULT 0,
            "ReservedQuantity" INTEGER NOT NULL DEFAULT 0,
            "AverageUnitCost" REAL NOT NULL DEFAULT 0,
            "RowVersion" INTEGER NOT NULL DEFAULT 1,
            "UpdatedAtUtc" TEXT NOT NULL
        )
        """;
    await ensureInv.ExecuteNonQueryAsync();
}

await using (var inv = c.CreateCommand())
{
    inv.CommandText = """
        INSERT INTO InventoryStocks
            (ProductId, QuantityOnHand, ReservedQuantity, AverageUnitCost, RowVersion, UpdatedAtUtc)
        VALUES ($id, $stock, 0, 5, 1, datetime('now'))
        ON CONFLICT(ProductId) DO UPDATE SET
            QuantityOnHand = $stock,
            RowVersion = InventoryStocks.RowVersion + 1,
            UpdatedAtUtc = datetime('now')
        """;
    inv.Parameters.AddWithValue("$id", productId);
    inv.Parameters.AddWithValue("$stock", Stock);
    await inv.ExecuteNonQueryAsync();
}

Console.WriteLine($"OK productId={productId} codigo={Codigo} stock={Stock} nombre={Nombre}");
return 0;
