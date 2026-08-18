using GrunflexPOS.API.Data;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Hosting;

public static class CommerceInventorySchemaInitializer
{
    public static async Task EnsureAsync(PosCommerceDbContext db, ILogger log, CancellationToken ct = default)
    {
        var provider = db.Database.ProviderName ?? "";
        var isSqlite = provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
        var isNpgsql = provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);

        if (!isSqlite && !isNpgsql)
        {
            log.LogWarning("inventory.schema skip unknown provider={P}", provider);
            return;
        }

        if (isSqlite)
            await EnsureSqliteAsync(db, ct);
        else
            await EnsurePostgresAsync(db, ct);

        await BackfillFromProductosAsync(db, ct);
        await AlignProductosStockFromInventoryAsync(db, ct);
        log.LogInformation("inventory.schema ensured provider={P}", provider);
    }

    /// <summary>Repara drift legacy: Productos.Stock ← InventoryStocks (autoridad transaccional).</summary>
    private static async Task AlignProductosStockFromInventoryAsync(PosCommerceDbContext db, CancellationToken ct)
    {
        var isSqlite = db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
        if (isSqlite)
        {
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE "Productos"
                SET "Stock" = (
                    SELECT "QuantityOnHand" FROM "InventoryStocks" s WHERE s."ProductId" = "Productos"."Id"
                )
                WHERE EXISTS (
                    SELECT 1 FROM "InventoryStocks" s
                    WHERE s."ProductId" = "Productos"."Id" AND s."QuantityOnHand" <> "Productos"."Stock"
                );
                """, ct);
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE "Productos" p
                SET "Stock" = s."QuantityOnHand"
                FROM "InventoryStocks" s
                WHERE s."ProductId" = p."Id" AND s."QuantityOnHand" <> p."Stock";
                """, ct);
        }
    }

    private static async Task EnsureSqliteAsync(PosCommerceDbContext db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "InventoryStocks" (
                "ProductId" INTEGER NOT NULL PRIMARY KEY,
                "QuantityOnHand" INTEGER NOT NULL DEFAULT 0,
                "ReservedQuantity" INTEGER NOT NULL DEFAULT 0,
                "AverageUnitCost" REAL NOT NULL DEFAULT 0,
                "RowVersion" INTEGER NOT NULL DEFAULT 1,
                "UpdatedAtUtc" TEXT NOT NULL
            );
            """, ct);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "InventoryMovements" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ProductId" INTEGER NOT NULL,
                "MovementType" INTEGER NOT NULL,
                "QuantityDelta" INTEGER NOT NULL,
                "QuantityAfter" INTEGER NOT NULL,
                "UnitCost" REAL NOT NULL,
                "TotalCost" REAL NOT NULL,
                "AverageUnitCostAfter" REAL NOT NULL,
                "ReferenceType" INTEGER NOT NULL,
                "ReferenceId" TEXT NULL,
                "TerminalId" TEXT NULL,
                "TerminalCode" TEXT NULL,
                "UserId" TEXT NULL,
                "UserSessionId" TEXT NULL,
                "BranchId" TEXT NULL,
                "CajaId" TEXT NULL,
                "RequestId" TEXT NULL,
                "CreatedAtUtc" TEXT NOT NULL
            );
            """, ct);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_InventoryMovements_ProductId"
            ON "InventoryMovements" ("ProductId");
            """, ct);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_InventoryMovements_CreatedAtUtc"
            ON "InventoryMovements" ("CreatedAtUtc");
            """, ct);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "InventoryReservations" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ProductId" INTEGER NOT NULL,
                "Quantity" INTEGER NOT NULL,
                "State" INTEGER NOT NULL,
                "ExpiresAtUtc" TEXT NOT NULL,
                "TerminalId" TEXT NULL,
                "ReferenceId" TEXT NULL,
                "CreatedAtUtc" TEXT NOT NULL
            );
            """, ct);
    }

    private static async Task EnsurePostgresAsync(PosCommerceDbContext db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "InventoryStocks" (
                "ProductId" INTEGER NOT NULL PRIMARY KEY,
                "QuantityOnHand" INTEGER NOT NULL DEFAULT 0,
                "ReservedQuantity" INTEGER NOT NULL DEFAULT 0,
                "AverageUnitCost" NUMERIC(18,4) NOT NULL DEFAULT 0,
                "RowVersion" BIGINT NOT NULL DEFAULT 1,
                "UpdatedAtUtc" TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """, ct);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "InventoryMovements" (
                "Id" UUID NOT NULL PRIMARY KEY,
                "ProductId" INTEGER NOT NULL,
                "MovementType" INTEGER NOT NULL,
                "QuantityDelta" INTEGER NOT NULL,
                "QuantityAfter" INTEGER NOT NULL,
                "UnitCost" NUMERIC(18,4) NOT NULL,
                "TotalCost" NUMERIC(18,4) NOT NULL,
                "AverageUnitCostAfter" NUMERIC(18,4) NOT NULL,
                "ReferenceType" INTEGER NOT NULL,
                "ReferenceId" TEXT NULL,
                "TerminalId" UUID NULL,
                "TerminalCode" TEXT NULL,
                "UserId" UUID NULL,
                "UserSessionId" UUID NULL,
                "BranchId" UUID NULL,
                "CajaId" UUID NULL,
                "RequestId" TEXT NULL,
                "CreatedAtUtc" TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """, ct);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_InventoryMovements_ProductId"
            ON "InventoryMovements" ("ProductId");
            """, ct);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "InventoryReservations" (
                "Id" UUID NOT NULL PRIMARY KEY,
                "ProductId" INTEGER NOT NULL,
                "Quantity" INTEGER NOT NULL,
                "State" INTEGER NOT NULL,
                "ExpiresAtUtc" TIMESTAMPTZ NOT NULL,
                "TerminalId" UUID NULL,
                "ReferenceId" TEXT NULL,
                "CreatedAtUtc" TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """, ct);
    }

    private static async Task BackfillFromProductosAsync(PosCommerceDbContext db, CancellationToken ct)
    {
        var isSqlite = db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
        if (isSqlite)
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT OR IGNORE INTO "InventoryStocks"
                    ("ProductId","QuantityOnHand","ReservedQuantity","AverageUnitCost","RowVersion","UpdatedAtUtc")
                SELECT "Id","Stock",0,
                       CASE WHEN "Costo" > 0 THEN "Costo" ELSE 0 END,
                       1, datetime('now')
                FROM "Productos";
                """, ct);
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "InventoryStocks"
                    ("ProductId","QuantityOnHand","ReservedQuantity","AverageUnitCost","RowVersion","UpdatedAtUtc")
                SELECT "Id","Stock",0,
                       CASE WHEN "Costo" > 0 THEN "Costo" ELSE 0 END,
                       1, NOW()
                FROM "Productos"
                ON CONFLICT ("ProductId") DO NOTHING;
                """, ct);
        }
    }
}
