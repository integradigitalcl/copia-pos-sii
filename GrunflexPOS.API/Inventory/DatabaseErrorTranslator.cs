using Microsoft.Data.Sqlite;
using Npgsql;

namespace GrunflexPOS.API.Inventory;

public static class DatabaseErrorTranslator
{
    public const string SerializationFailure = "SERIALIZATION_FAILURE";
    public const string Deadlock = "DEADLOCK";
    public const string UniqueViolation = "UNIQUE_VIOLATION";
    public const string Transient = "TRANSIENT_DB";
    public const string Busy = "DATABASE_BUSY";

    public static string OperatorMessage(string errorCode) => errorCode switch
    {
        SerializationFailure =>
            "Otra terminal completó un cambio de inventario primero. Reintente la operación.",
        Deadlock =>
            "Conflicto temporal entre cajas. Reintente en unos segundos.",
        UniqueViolation =>
            "La operación ya fue registrada (duplicado). Verifique el ticket o reintente con nuevo RequestId.",
        Busy or Transient =>
            "Conexión temporal con la base de datos. Reintente.",
        _ => "No se pudo completar la operación de inventario. Reintente."
    };

    public static bool IsRetriable(string errorCode) =>
        errorCode is SerializationFailure or Deadlock or Busy or Transient;

    public static PosConcurrencyException? TryTranslate(Exception ex)
    {
        for (var cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur is PostgresException pg)
            {
                var code = MapPostgres(pg.SqlState);
                if (code != null)
                    return new PosConcurrencyException(code, OperatorMessage(code), pg);
            }

            if (cur is SqliteException sqlite)
            {
                var code = MapSqlite(sqlite);
                if (code != null)
                    return new PosConcurrencyException(code, OperatorMessage(code), sqlite);
            }
        }

        return null;
    }

    public static string? Classify(Exception ex)
    {
        for (var cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur is PostgresException pg)
                return MapPostgres(pg.SqlState);
            if (cur is SqliteException sqlite)
                return MapSqlite(sqlite);
        }

        return null;
    }

    private static string? MapPostgres(string? sqlState) => sqlState switch
    {
        "40001" => SerializationFailure,
        "40P01" => Deadlock,
        "23505" => UniqueViolation,
        "57P01" or "57P02" or "57P03" or "08000" or "08003" or "08006" or "53300" => Transient,
        _ => null
    };

    private static string? MapSqlite(SqliteException ex) => ex.SqliteErrorCode switch
    {
        5 => Busy,
        6 => Busy,
        19 when ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) => UniqueViolation,
        _ => ex.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase) ? Busy : null
    };
}
