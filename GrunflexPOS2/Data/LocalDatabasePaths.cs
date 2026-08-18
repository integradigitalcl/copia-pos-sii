using System;
using System.IO;

namespace GrunflexPOS2.Data;

/// <summary>
/// Rutas de la base SQLite embebida.
///
/// Estrategia (Fase 1):
/// - Preferir <c>%ProgramData%\GrunflexPOS\data</c> (machine-wide). Esto es REQUERIDO para que
///   la API corriendo como servicio Windows (LocalSystem) pueda leer la misma BD que el POS UI.
/// - Si la BD aún no existe ahí pero sí en <c>%LocalAppData%</c> legacy, intentar migración
///   transparente la primera vez que arranca el POS.
/// - Si la migración falla (permisos), seguir usando LocalAppData para no bloquear al usuario.
/// </summary>
public static class LocalDatabasePaths
{
    public const string EnvOverride = "GRUNFLEX_DATA_DIR";
    private const string AppFolderName = "GrunflexPOS";

    public static string ProgramDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppFolderName,
            "data");

    public static string LocalAppDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName,
            "data");

    /// <summary>Carpeta de datos preferida en runtime. Resuelve env > programdata > localappdata.</summary>
    public static string DataDirectory
    {
        get
        {
            if (TerminalConfigProbe.IsTerminalApiOnlyClient())
            {
                EnsureTerminalShadowParentExists();
                return Path.GetDirectoryName(TerminalClientShadowDatabasePath)!;
            }

            var env = Environment.GetEnvironmentVariable(EnvOverride);
            if (!string.IsNullOrWhiteSpace(env))
            {
                try { Directory.CreateDirectory(env); return env; }
                catch { }
            }

            // Si hay BD en ProgramData, esa manda
            var pd = ProgramDataDirectory;
            try
            {
                if (File.Exists(Path.Combine(pd, "grunflex.db")))
                    return pd;
            }
            catch { }

            // Si la BD vive en LocalAppData (legacy), preferirla para no romper datos del usuario
            var lad = LocalAppDataDirectory;
            try
            {
                if (File.Exists(Path.Combine(lad, "grunflex.db")))
                    return lad;
            }
            catch { }

            // Instalación nueva: usar ProgramData. Si no se puede (raro), caer a LocalAppData.
            try
            {
                Directory.CreateDirectory(pd);
                return pd;
            }
            catch
            {
                Directory.CreateDirectory(lad);
                return lad;
            }
        }
    }

    public static string DatabaseFilePath => Path.Combine(DataDirectory, "grunflex.db");

    public static string DefaultConnectionString => $"Data Source={DatabaseFilePath};Cache=Shared";

    /// <summary>
    /// BD sombra local para caja adicional en modo API-only (sin UNC). Misma carpeta
    /// legacy LocalAppData para no requerir permisos de ProgramData en el cliente.
    /// </summary>
    public static string TerminalClientShadowDatabasePath =>
        Path.Combine(LocalAppDataDirectory, "terminal_shadow.db");

    public static string TerminalClientShadowConnectionString =>
        $"Data Source={TerminalClientShadowDatabasePath};Cache=Shared";

    /// <summary>Histórico de cortes solo en esta máquina (sin UNC). Ver <see cref="AppConfig.CortesHistoricoLocalPath"/>.</summary>
    public static string DefaultCortesHistoricoLocalJsonPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName,
            "cortes_historico_local.json");

    /// <summary>Crea la carpeta del SQLite sombra de terminal (caja adicional API-only).</summary>
    public static void EnsureTerminalShadowParentExists()
    {
        var dir = Path.GetDirectoryName(TerminalClientShadowDatabasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public static void EnsureDataDirectoryExists()
    {
        Directory.CreateDirectory(DataDirectory);
        if (!GrunflexDataDirectoryAcl.VerifyWriteAccess(DataDirectory))
            GrunflexDataDirectoryAcl.TryRepairCommerceDatabase();
    }

    /// <summary>
    /// Migra <c>grunflex.db</c> y archivos asociados (-wal, -shm, backups) desde LocalAppData
    /// a ProgramData si: (1) existe en LocalAppData, (2) no existe en ProgramData,
    /// (3) ProgramData es escribible. Best-effort, no lanza.
    /// </summary>
    public static bool MigrateLegacyToProgramDataIfNeeded()
    {
        try
        {
            var src = LocalAppDataDirectory;
            var dst = ProgramDataDirectory;

            var srcDb = Path.Combine(src, "grunflex.db");
            var dstDb = Path.Combine(dst, "grunflex.db");

            if (!File.Exists(srcDb)) return false;
            if (File.Exists(dstDb)) return false;

            Directory.CreateDirectory(dst);

            // Probar escribibilidad
            var testFile = Path.Combine(dst, ".grunflex-write-test");
            try { File.WriteAllText(testFile, "ok"); File.Delete(testFile); }
            catch { return false; }

            // Copiar BD principal + archivos auxiliares de SQLite si existen
            foreach (var name in new[] { "grunflex.db", "grunflex.db-wal", "grunflex.db-shm" })
            {
                var s = Path.Combine(src, name);
                if (File.Exists(s))
                {
                    var d = Path.Combine(dst, name);
                    try { File.Copy(s, d, overwrite: false); } catch { }
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
