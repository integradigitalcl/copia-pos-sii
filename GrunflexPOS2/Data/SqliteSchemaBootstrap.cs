using System;
using System.Data;
using GrunflexPOS2.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS2.Data;

internal static class SqliteSchemaBootstrap
{
    private const string InitialMigration = "20260428002237_InitialSqlite";
    private const string ConsumoMigration = "20260509210347_AgregarConsumoPersonalDetalleCodigo";

    /// <summary>
    /// Aplica migraciones EF en la BD local (no UNC).
    /// Si la API u otro componente creó tablas sin <c>__EFMigrationsHistory</c>, se alinea el historial.
    /// </summary>
    public static void EnsureMigrated(GrunflexDbContext db, bool skipUnc)
    {
        var cs = db.Database.GetConnectionString() ?? string.Empty;
        if (skipUnc && cs.Contains(@"\\", StringComparison.Ordinal))
            return;

        try
        {
            BaselineIfCreatedOutsideEf(db);
            db.Database.Migrate();
        }
        catch (Exception ex) when (TryRecoverFromAlreadyExists(db, ex))
        {
            /* recuperado tras alinear historial EF */
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("SqliteSchemaBootstrap.Migrate", ex);
            throw new InvalidOperationException(
                "No se pudo preparar la base de datos: " + ex.GetBaseException().Message, ex);
        }
    }

    private static bool TryRecoverFromAlreadyExists(GrunflexDbContext db, Exception ex)
    {
        if (!IsAlreadyExists(ex))
            return false;

        try
        {
            BaselineIfCreatedOutsideEf(db);
            db.Database.Migrate();
            return true;
        }
        catch (Exception retryEx)
        {
            PosDiagnostics.Log("SqliteSchemaBootstrap.Recover", retryEx);
            return false;
        }
    }

    /// <summary>
    /// GrunflexPOS.API puede crear <c>grunflex.db</c> con EnsureCreated antes del primer arranque del POS.
    /// </summary>
    private static void BaselineIfCreatedOutsideEf(GrunflexDbContext db)
    {
        if (HasMigration(db, InitialMigration))
            return;

        if (!TableExists(db, "Empresas"))
            return;

        EnsurePosOnlyTables(db);
        EnsureMigrationHistoryTable(db);
        StampMigration(db, InitialMigration);

        if (ColumnExists(db, "Ventas", "EsConsumoPersonal"))
            StampMigration(db, ConsumoMigration);
    }

    private static void EnsurePosOnlyTables(GrunflexDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "Categorias" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Categorias" PRIMARY KEY AUTOINCREMENT,
                "Nombre" TEXT NOT NULL
            );
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "Configuraciones" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Configuraciones" PRIMARY KEY,
                "Clave" TEXT NOT NULL,
                "Valor" TEXT NOT NULL
            );
            """);
    }

    private static void EnsureMigrationHistoryTable(GrunflexDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL
            );
            """);
    }

    private static void StampMigration(GrunflexDbContext db, string migrationId)
    {
        if (HasMigration(db, migrationId))
            return;

        db.Database.ExecuteSqlRaw(
            """
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ({0}, {1});
            """,
            migrationId,
            "8.0.11");
    }

    private static bool HasMigration(GrunflexDbContext db, string migrationId)
    {
        if (!TableExists(db, "__EFMigrationsHistory"))
            return false;

        var conn = db.Database.GetDbConnection();
        var openedHere = conn.State != ConnectionState.Open;
        if (openedHere)
            conn.Open();

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(1) FROM "__EFMigrationsHistory" WHERE "MigrationId" = $id;
                """;
            var p = cmd.CreateParameter();
            p.ParameterName = "$id";
            p.Value = migrationId;
            cmd.Parameters.Add(p);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
        finally
        {
            if (openedHere)
                conn.Close();
        }
    }

    private static bool TableExists(GrunflexDbContext db, string table)
    {
        var conn = db.Database.GetDbConnection();
        var openedHere = conn.State != ConnectionState.Open;
        if (openedHere)
            conn.Open();

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = $name;
                """;
            var p = cmd.CreateParameter();
            p.ParameterName = "$name";
            p.Value = table;
            cmd.Parameters.Add(p);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
        finally
        {
            if (openedHere)
                conn.Close();
        }
    }

    private static bool ColumnExists(GrunflexDbContext db, string table, string column)
    {
        var conn = db.Database.GetDbConnection();
        var openedHere = conn.State != ConnectionState.Open;
        if (openedHere)
            conn.Open();

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(1) FROM pragma_table_info('{table.Replace("'", "''")}') WHERE name = $name;";
            var p = cmd.CreateParameter();
            p.ParameterName = "$name";
            p.Value = column;
            cmd.Parameters.Add(p);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
        finally
        {
            if (openedHere)
                conn.Close();
        }
    }

    private static bool IsAlreadyExists(Exception ex)
    {
        for (var cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur is SqliteException sql &&
                sql.SqliteErrorCode == 1 &&
                sql.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
