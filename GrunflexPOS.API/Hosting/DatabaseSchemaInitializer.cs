using Grunflex.Licensing.Security;
using GrunflexPOS.API.Data;
using GrunflexPOS.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace GrunflexPOS.API.Hosting;

public sealed class DatabaseSchemaInitializer(IServiceProvider services, ILogger<DatabaseSchemaInitializer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await DatabaseSchemaBootstrap.RunAsync(services, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database schema initialization failed");
            Log.Error(ex, "Database schema initialization failed");
        }
    }
}

internal static class DatabaseSchemaBootstrap
{
    public static async Task RunAsync(IServiceProvider services, CancellationToken ct)
    {
// Esquema local (SQLite o PostgreSQL) sin migraciones empaquetadas.
using (var scope = services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
    try
    {
        await db.Database.EnsureCreatedAsync(ct);
    }
    catch (Exception ex) when (IsSchemaAlreadyApplied(ex))
    {
        /* arranque idempotente: otra instancia o EnsureCreated previo */
    }

    // EnsureCreated no agrega tablas nuevas si la BD ya existía.
    if (db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "LicenseIssuerRecords" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_LicenseIssuerRecords" PRIMARY KEY,
                "ActivationId" TEXT NOT NULL,
                "CustomerName" TEXT NOT NULL,
                "BusinessName" TEXT NOT NULL,
                "LicenseType" TEXT NOT NULL,
                "NumberOfBoxes" INTEGER NOT NULL,
                "ExpUtc" TEXT NOT NULL,
                "Multicaja" INTEGER NOT NULL,
                "OnlineSupport" INTEGER NOT NULL,
                "CloudBackup" INTEGER NOT NULL,
                "PrioritySupport" INTEGER NOT NULL,
                "LicenseToken" TEXT NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_LicenseIssuerRecords_ActivationId"
            ON "LicenseIssuerRecords" ("ActivationId");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_LicenseIssuerRecords_CreatedAtUtc"
            ON "LicenseIssuerRecords" ("CreatedAtUtc");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "StoredBackups" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_StoredBackups" PRIMARY KEY,
                "ActivationId" TEXT NOT NULL,
                "OriginalFileName" TEXT NOT NULL,
                "StorageFileName" TEXT NOT NULL,
                "SizeBytes" INTEGER NOT NULL,
                "Sha256Hex" TEXT NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_StoredBackups_ActivationId"
            ON "StoredBackups" ("ActivationId");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_StoredBackups_CreatedAtUtc"
            ON "StoredBackups" ("CreatedAtUtc");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "SupportTickets" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SupportTickets" PRIMARY KEY,
                "ActivationId" TEXT NOT NULL,
                "Subject" TEXT NOT NULL,
                "Body" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_SupportTickets_ActivationId"
            ON "SupportTickets" ("ActivationId");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_SupportTickets_CreatedAtUtc"
            ON "SupportTickets" ("CreatedAtUtc");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "IssuerClientRecords" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_IssuerClientRecords" PRIMARY KEY,
                "ClientCode" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Business" TEXT NOT NULL,
                "Email" TEXT NOT NULL,
                "Phone" TEXT NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_IssuerClientRecords_ClientCode"
            ON "IssuerClientRecords" ("ClientCode");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "IssuerActivationRecords" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_IssuerActivationRecords" PRIMARY KEY,
                "ActivationId" TEXT NOT NULL,
                "CustomerDisplay" TEXT NOT NULL,
                "DeviceName" TEXT NOT NULL,
                "HardwareId" TEXT NOT NULL,
                "ActivatedAtUtc" TEXT NOT NULL,
                "LastSeenUtc" TEXT NOT NULL,
                "Status" TEXT NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_IssuerActivationRecords_ActivationId"
            ON "IssuerActivationRecords" ("ActivationId");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_IssuerActivationRecords_LastSeenUtc"
            ON "IssuerActivationRecords" ("LastSeenUtc");
            """);

        // Fase 5.1: registro de terminales server-side
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "TerminalRegistrations" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_TerminalRegistrations" PRIMARY KEY,
                "MachineFingerprint" TEXT NOT NULL,
                "MachineName" TEXT NOT NULL,
                "Version" TEXT NOT NULL,
                "ActivationId" TEXT NOT NULL,
                "FirstSeenUtc" TEXT NOT NULL,
                "LastHeartbeatUtc" TEXT NOT NULL,
                "Active" INTEGER NOT NULL,
                "IpAddress" TEXT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_TerminalRegistrations_MachineFingerprint"
            ON "TerminalRegistrations" ("MachineFingerprint");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_TerminalRegistrations_LastHeartbeatUtc"
            ON "TerminalRegistrations" ("LastHeartbeatUtc");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_TerminalRegistrations_Active"
            ON "TerminalRegistrations" ("Active");
            """);
        await EnsureIdempotencyRecordsSqliteAsync(db);
        await EnsureTerminalEnterpriseSqliteAsync(db);
        await EnsureLicenseIssuerSchemaAsync(db);
    }
    else if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "LicenseIssuerRecords" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "ActivationId" varchar(80) NOT NULL,
                "CustomerName" varchar(150) NOT NULL,
                "BusinessName" varchar(150) NOT NULL,
                "LicenseType" varchar(40) NOT NULL,
                "NumberOfBoxes" integer NOT NULL,
                "ExpUtc" timestamp with time zone NOT NULL,
                "Multicaja" boolean NOT NULL,
                "OnlineSupport" boolean NOT NULL,
                "CloudBackup" boolean NOT NULL,
                "PrioritySupport" boolean NOT NULL,
                "LicenseToken" varchar(4096) NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_LicenseIssuerRecords_ActivationId"
            ON "LicenseIssuerRecords" ("ActivationId");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_LicenseIssuerRecords_CreatedAtUtc"
            ON "LicenseIssuerRecords" ("CreatedAtUtc");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "StoredBackups" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "ActivationId" varchar(80) NOT NULL,
                "OriginalFileName" varchar(260) NOT NULL,
                "StorageFileName" varchar(260) NOT NULL,
                "SizeBytes" bigint NOT NULL,
                "Sha256Hex" varchar(64) NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_StoredBackups_ActivationId"
            ON "StoredBackups" ("ActivationId");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_StoredBackups_CreatedAtUtc"
            ON "StoredBackups" ("CreatedAtUtc");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "SupportTickets" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "ActivationId" varchar(80) NOT NULL,
                "Subject" varchar(200) NOT NULL,
                "Body" varchar(8000) NOT NULL,
                "Status" varchar(40) NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_SupportTickets_ActivationId"
            ON "SupportTickets" ("ActivationId");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_SupportTickets_CreatedAtUtc"
            ON "SupportTickets" ("CreatedAtUtc");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "IssuerClientRecords" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "ClientCode" varchar(32) NOT NULL,
                "Name" varchar(150) NOT NULL,
                "Business" varchar(150) NOT NULL,
                "Email" varchar(200) NOT NULL,
                "Phone" varchar(40) NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_IssuerClientRecords_ClientCode"
            ON "IssuerClientRecords" ("ClientCode");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "IssuerActivationRecords" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "ActivationId" varchar(80) NOT NULL,
                "CustomerDisplay" varchar(300) NOT NULL,
                "DeviceName" varchar(120) NOT NULL,
                "HardwareId" varchar(120) NOT NULL,
                "ActivatedAtUtc" timestamp with time zone NOT NULL,
                "LastSeenUtc" timestamp with time zone NOT NULL,
                "Status" varchar(40) NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_IssuerActivationRecords_ActivationId"
            ON "IssuerActivationRecords" ("ActivationId");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_IssuerActivationRecords_LastSeenUtc"
            ON "IssuerActivationRecords" ("LastSeenUtc");
            """);
        await EnsureIdempotencyRecordsPostgresAsync(db);
        await EnsureTerminalEnterprisePostgresAsync(db);
        await EnsureLicenseIssuerSchemaAsync(db);
    }
}

// Idempotencia ventas multicaja sobre la BD operacional del POS (grunflex.db).
try
{
    using var scopePos = services.CreateScope();
    var posDb = scopePos.ServiceProvider.GetRequiredService<PosCommerceDbContext>();
    if (posDb.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
    {
        await EnsureCommerceCoreSqliteAsync(posDb);
        await posDb.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "MulticajaVentaIdempotency" (
                "RequestId" TEXT NOT NULL PRIMARY KEY,
                "VentaId" TEXT NOT NULL,
                "NumeroTicket" INTEGER NOT NULL,
                "CreatedUtc" TEXT NOT NULL
            );
            """);
        await posDb.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "MulticajaAnulacionIdempotency" (
                "RequestId" TEXT NOT NULL PRIMARY KEY,
                "NumeroTicket" INTEGER NOT NULL,
                "CreatedUtc" TEXT NOT NULL
            );
            """);
        await posDb.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "MulticajaDevolucionIdempotency" (
                "RequestId" TEXT NOT NULL PRIMARY KEY,
                "NumeroTicket" INTEGER NOT NULL,
                "MontoDevuelto" REAL NOT NULL,
                "NuevoTotalVenta" REAL NOT NULL,
                "CreatedUtc" TEXT NOT NULL
            );
            """);
        await posDb.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "MulticajaCierreIdempotency" (
                "RequestId" TEXT NOT NULL PRIMARY KEY,
                "CajaSesionId" TEXT NOT NULL,
                "Esperado" REAL NOT NULL,
                "MontoContado" REAL NOT NULL,
                "Diferencia" REAL NOT NULL,
                "CreatedUtc" TEXT NOT NULL
            );
            """);
        await posDb.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "MulticajaMovimientoCajaIdempotency" (
                "RequestId" TEXT NOT NULL PRIMARY KEY,
                "CajaSesionId" TEXT NOT NULL,
                "Tipo" TEXT NOT NULL,
                "Monto" REAL NOT NULL,
                "TotalIngresos" REAL NOT NULL,
                "TotalRetiros" REAL NOT NULL,
                "TotalVentas" REAL NOT NULL,
                "CreatedUtc" TEXT NOT NULL
            );
            """);

        var invLog = scopePos.ServiceProvider.GetRequiredService<ILogger<DatabaseSchemaInitializer>>();
        await CommerceInventorySchemaInitializer.EnsureAsync(posDb, invLog, ct);
        await CommerceFirstRunBootstrap.EnsureAsync(services, ct);
    }
}
catch (Exception ex)
{
    Log.Warning(ex, "No se pudo asegurar tabla MulticajaVentaIdempotency en la BD POS");
}

try
{
    using var scopePwd = services.CreateScope();
    var posDbPwd = scopePwd.ServiceProvider.GetRequiredService<PosCommerceDbContext>();
    var pwdLog = scopePwd.ServiceProvider.GetRequiredService<ILogger<DatabaseSchemaInitializer>>();
    var commerceUsers = await posDbPwd.Usuarios.ToListAsync(ct).ConfigureAwait(false);
    var migrated = UserPasswordMigration.MigrateLegacyHashes(
        commerceUsers.Select(u => (u.Password, (Action<string>)(h => u.Password = h))).ToList());
    if (migrated > 0)
    {
        await posDbPwd.SaveChangesAsync(ct).ConfigureAwait(false);
        pwdLog.LogInformation("user-password-migration: {Count} contraseña(s) legacy migradas a BCrypt", migrated);
    }
}
catch (Exception ex)
{
    Log.Warning(ex, "No se pudo migrar contraseñas legacy a BCrypt en BD POS");
}

    }

    /// <summary>Crea las tablas base de comercio en SQLite cuando la BD del POS aún no tiene esquema.</summary>
    private static async Task EnsureCommerceCoreSqliteAsync(PosCommerceDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Empresas" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Empresas" PRIMARY KEY,
                "Nombre" TEXT NOT NULL,
                "FechaCreacion" TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "Cajas" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Cajas" PRIMARY KEY,
                "Nombre" TEXT NOT NULL,
                "EmpresaId" TEXT NOT NULL,
                "Activa" INTEGER NOT NULL,
                "FechaCreacion" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_Cajas_EmpresaId" ON "Cajas" ("EmpresaId");
            CREATE TABLE IF NOT EXISTS "Usuarios" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Usuarios" PRIMARY KEY,
                "Username" TEXT NOT NULL,
                "Password" TEXT NOT NULL,
                "Nombre" TEXT NOT NULL,
                "Rol" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Usuarios_Username" ON "Usuarios" ("Username");
            CREATE TABLE IF NOT EXISTS "CajaSesiones" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CajaSesiones" PRIMARY KEY,
                "CajaId" TEXT NOT NULL,
                "NumeroCaja" INTEGER NOT NULL,
                "Cajero" TEXT NOT NULL,
                "UsuarioAperturaId" TEXT NOT NULL,
                "UsuarioCierreId" TEXT NULL,
                "FechaApertura" TEXT NOT NULL,
                "MontoApertura" TEXT NOT NULL,
                "FechaCierre" TEXT NULL,
                "MontoCierre" TEXT NULL,
                "Diferencia" TEXT NOT NULL,
                "TotalVentas" TEXT NOT NULL,
                "TotalIngresos" TEXT NOT NULL,
                "TotalRetiros" TEXT NOT NULL,
                "Abierta" INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_CajaSesiones_CajaId" ON "CajaSesiones" ("CajaId");
            CREATE TABLE IF NOT EXISTS "MovimientosCaja" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_MovimientosCaja" PRIMARY KEY,
                "CajaSesionId" TEXT NOT NULL,
                "Fecha" TEXT NOT NULL,
                "Tipo" TEXT NOT NULL,
                "Monto" TEXT NOT NULL,
                "Descripcion" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_MovimientosCaja_CajaSesionId" ON "MovimientosCaja" ("CajaSesionId");
            CREATE TABLE IF NOT EXISTS "Productos" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Productos" PRIMARY KEY AUTOINCREMENT,
                "Nombre" TEXT NOT NULL,
                "Costo" TEXT NOT NULL,
                "Precio" TEXT NOT NULL,
                "Stock" INTEGER NOT NULL,
                "CodigoBarras" TEXT NOT NULL,
                "PrecioMayoreo" TEXT NOT NULL,
                "InvMinimo" INTEGER NOT NULL,
                "InvMaximo" INTEGER NOT NULL,
                "TipoVenta" TEXT NOT NULL,
                "Departamento" TEXT NOT NULL,
                "CategoriaId" INTEGER NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Productos_CodigoBarras" ON "Productos" ("CodigoBarras");
            CREATE TABLE IF NOT EXISTS "Ventas" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Ventas" PRIMARY KEY,
                "NumeroTicket" INTEGER NOT NULL,
                "Fecha" TEXT NOT NULL,
                "Total" TEXT NOT NULL,
                "NumeroCaja" INTEGER NOT NULL,
                "CajaId" TEXT NOT NULL,
                "Cajero" TEXT NOT NULL,
                "Cliente" TEXT NOT NULL,
                "MetodoPago" TEXT NOT NULL,
                "EstaAnulada" INTEGER NOT NULL,
                "FechaAnulacion" TEXT NULL,
                "UsuarioId" TEXT NOT NULL,
                "CajaSesionId" TEXT NOT NULL,
                "EsConsumoPersonal" INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS "DetalleVentas" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_DetalleVentas" PRIMARY KEY,
                "VentaId" TEXT NOT NULL,
                "CodigoBarras" TEXT NULL,
                "Producto" TEXT NOT NULL,
                "Cantidad" INTEGER NOT NULL,
                "Precio" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_DetalleVentas_VentaId" ON "DetalleVentas" ("VentaId");
            """);
    }

    private static bool IsSchemaAlreadyApplied(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is SqliteException sql &&
                sql.SqliteErrorCode == 1 &&
                sql.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static async Task EnsureIdempotencyRecordsSqliteAsync(ApiDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "IdempotencyRecords" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "RequestId" TEXT NOT NULL,
                "OperationType" TEXT NOT NULL,
                "RequestHash" TEXT NOT NULL,
                "TerminalId" TEXT NOT NULL,
                "CajaId" TEXT NULL,
                "Status" INTEGER NOT NULL,
                "ResponseCode" INTEGER NULL,
                "ResponsePayload" TEXT NULL,
                "ResourceType" TEXT NULL,
                "ResourceId" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "CompletedAt" TEXT NULL,
                "ExpiresAt" TEXT NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_IdempotencyRecords_RequestId_OperationType"
            ON "IdempotencyRecords" ("RequestId", "OperationType");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_IdempotencyRecords_ExpiresAt"
            ON "IdempotencyRecords" ("ExpiresAt");
            """);
    }

    private static async Task EnsureIdempotencyRecordsPostgresAsync(ApiDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "IdempotencyRecords" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "RequestId" varchar(64) NOT NULL,
                "OperationType" varchar(64) NOT NULL,
                "RequestHash" varchar(64) NOT NULL,
                "TerminalId" varchar(120) NOT NULL,
                "CajaId" uuid NULL,
                "Status" integer NOT NULL,
                "ResponseCode" integer NULL,
                "ResponsePayload" text NULL,
                "ResourceType" varchar(64) NULL,
                "ResourceId" varchar(64) NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "CompletedAt" timestamp with time zone NULL,
                "ExpiresAt" timestamp with time zone NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_IdempotencyRecords_RequestId_OperationType"
            ON "IdempotencyRecords" ("RequestId", "OperationType");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_IdempotencyRecords_ExpiresAt"
            ON "IdempotencyRecords" ("ExpiresAt");
            """);
    }

    private static async Task EnsureTerminalEnterpriseSqliteAsync(ApiDbContext db)
    {
        await TryAddColumnSqliteAsync(db, "TerminalRegistrations", "InstallationId", "TEXT NULL");
        await TryAddColumnSqliteAsync(db, "TerminalRegistrations", "CajaId", "TEXT NULL");
        await TryAddColumnSqliteAsync(db, "TerminalRegistrations", "BranchId", "TEXT NULL");
        await TryAddColumnSqliteAsync(db, "TerminalRegistrations", "TerminalTokenHash", "TEXT NULL");
        await TryAddColumnSqliteAsync(db, "TerminalRegistrations", "DisplayName", "TEXT NULL");
        await TryAddColumnSqliteAsync(db, "TerminalRegistrations", "DeactivatedAtUtc", "TEXT NULL");
        await TryAddColumnSqliteAsync(db, "TerminalRegistrations", "DeactivatedReason", "TEXT NULL");

        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_TerminalRegistrations_InstallationId"
            ON "TerminalRegistrations" ("InstallationId");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_TerminalRegistrations_CajaId"
            ON "TerminalRegistrations" ("CajaId");
            """);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "TerminalAuditLogs" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "TerminalId" TEXT NOT NULL,
                "EventType" TEXT NOT NULL,
                "Details" TEXT NULL,
                "IpAddress" TEXT NULL,
                "CreatedAtUtc" TEXT NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "TerminalHeartbeats" (
                "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "TerminalId" TEXT NOT NULL,
                "CajaId" TEXT NULL,
                "CurrentUserId" TEXT NULL,
                "CurrentSessionId" TEXT NULL,
                "LastSeenAtUtc" TEXT NOT NULL,
                "ClientVersion" TEXT NULL,
                "IpAddress" TEXT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_TerminalHeartbeats_TerminalId"
            ON "TerminalHeartbeats" ("TerminalId");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "MulticajaSyncChangeLogs" (
                "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "Domain" TEXT NOT NULL,
                "EntityId" TEXT NOT NULL,
                "ChangeType" TEXT NOT NULL,
                "ChangedAtUtc" TEXT NOT NULL,
                "IsDeleted" INTEGER NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_MulticajaSyncChangeLogs_ChangedAtUtc"
            ON "MulticajaSyncChangeLogs" ("ChangedAtUtc");
            """);
    }

    private static async Task EnsureTerminalEnterprisePostgresAsync(ApiDbContext db)
    {
        await TryAddColumnPostgresAsync(db, "TerminalRegistrations", "InstallationId", "uuid NULL");
        await TryAddColumnPostgresAsync(db, "TerminalRegistrations", "CajaId", "uuid NULL");
        await TryAddColumnPostgresAsync(db, "TerminalRegistrations", "BranchId", "uuid NULL");
        await TryAddColumnPostgresAsync(db, "TerminalRegistrations", "TerminalTokenHash", "varchar(64) NULL");
        await TryAddColumnPostgresAsync(db, "TerminalRegistrations", "DisplayName", "varchar(120) NULL");
        await TryAddColumnPostgresAsync(db, "TerminalRegistrations", "DeactivatedAtUtc", "timestamp with time zone NULL");
        await TryAddColumnPostgresAsync(db, "TerminalRegistrations", "DeactivatedReason", "varchar(500) NULL");

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "TerminalRegistrations" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "MachineFingerprint" varchar(120) NOT NULL,
                "MachineName" varchar(120) NOT NULL,
                "Version" varchar(40) NOT NULL,
                "ActivationId" varchar(80) NOT NULL,
                "FirstSeenUtc" timestamp with time zone NOT NULL,
                "LastHeartbeatUtc" timestamp with time zone NOT NULL,
                "Active" boolean NOT NULL,
                "IpAddress" varchar(64) NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_TerminalRegistrations_MachineFingerprint"
            ON "TerminalRegistrations" ("MachineFingerprint");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_TerminalRegistrations_InstallationId"
            ON "TerminalRegistrations" ("InstallationId");
            """);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "TerminalAuditLogs" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "TerminalId" uuid NOT NULL,
                "EventType" varchar(64) NOT NULL,
                "Details" text NULL,
                "IpAddress" varchar(64) NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "TerminalHeartbeats" (
                "Id" bigserial PRIMARY KEY,
                "TerminalId" uuid NOT NULL,
                "CajaId" uuid NULL,
                "CurrentUserId" uuid NULL,
                "CurrentSessionId" uuid NULL,
                "LastSeenAtUtc" timestamp with time zone NOT NULL,
                "ClientVersion" varchar(40) NULL,
                "IpAddress" varchar(64) NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_TerminalHeartbeats_TerminalId"
            ON "TerminalHeartbeats" ("TerminalId");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "MulticajaSyncChangeLogs" (
                "Id" bigserial PRIMARY KEY,
                "Domain" varchar(32) NOT NULL,
                "EntityId" varchar(64) NOT NULL,
                "ChangeType" varchar(32) NOT NULL,
                "ChangedAtUtc" timestamp with time zone NOT NULL,
                "IsDeleted" boolean NOT NULL
            );
            """);
    }

    private static async Task EnsureLicenseIssuerSchemaAsync(ApiDbContext db)
    {
        if (db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            await TryAddColumnSqliteAsync(
                db,
                "LicenseIssuerRecords",
                "OfflineGraceDays",
                "INTEGER NOT NULL DEFAULT 14");
        }
        else if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            await TryAddColumnPostgresAsync(
                db,
                "LicenseIssuerRecords",
                "OfflineGraceDays",
                "integer NOT NULL DEFAULT 14");
        }
    }

    private static async Task TryAddColumnSqliteAsync(ApiDbContext db, string table, string column, string ddl)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {ddl};");
        }
        catch
        {
            /* columna ya existe */
        }
    }

    private static async Task TryAddColumnPostgresAsync(ApiDbContext db, string table, string column, string ddl)
    {
        await db.Database.ExecuteSqlRawAsync(
            $"""ALTER TABLE "{table}" ADD COLUMN IF NOT EXISTS "{column}" {ddl};""");
    }
}
