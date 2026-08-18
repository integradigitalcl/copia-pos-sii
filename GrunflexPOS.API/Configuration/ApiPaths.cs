using System;
using System.IO;

namespace GrunflexPOS.API.Configuration;

/// <summary>
/// Resolución central de rutas de datos del lado API.
///
/// Reglas:
/// 1. Si la variable de entorno <c>GRUNFLEX_DATA_DIR</c> está definida y existe, se usa.
/// 2. Si la API corre como servicio de Windows (LocalSystem), se usa <c>%ProgramData%\GrunflexPOS\data</c>
///    (machine-wide, accesible por cualquier usuario y por el servicio).
/// 3. En modo consola interactiva (debug, dev), se usa <c>%LocalAppData%\GrunflexPOS\data</c>
///    para mantener compatibilidad con el comportamiento previo a la Fase 1.
///
/// Si existen datos legacy en <c>%LocalAppData%</c> y el destino "service" en <c>%ProgramData%</c>
/// está vacío, se migran automáticamente al primer arranque del servicio.
/// </summary>
public static class ApiPaths
{
    public const string EnvOverride   = "GRUNFLEX_DATA_DIR";
    private const string AppFolderName = "GrunflexPOS";

    /// <summary>Carpeta de datos donde se guarda la BD SQLite, secretos, PEM, logs.</summary>
    public static string DataDirectory => ResolveDataDirectory();

    public static string LogsDirectory =>
        Path.Combine(Path.GetDirectoryName(DataDirectory) ?? DataDirectory, "logs");

    public static string SqlitePath =>
        Path.Combine(DataDirectory, "grunflex_api.db");

    public static string SecretsFilePath =>
        Path.Combine(DataDirectory, "api.secrets.json");

    public static string LicensingPublicPemPath =>
        Path.Combine(DataDirectory, "licensing-public.pem");

    /// <summary>Carpeta para secretos no-secrets-json (certs HTTPS, etc).</summary>
    public static string SecretsDirectory
    {
        get
        {
            var dir = Path.Combine(Path.GetDirectoryName(DataDirectory) ?? DataDirectory, "secrets");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string ResolveDataDirectory()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvOverride);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            try
            {
                Directory.CreateDirectory(fromEnv);
                return fromEnv;
            }
            catch
            {
                // si falla, caemos al resolver por contexto
            }
        }

        string baseFolder;
        if (IsRunningAsWindowsService())
        {
            baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        }
        else
        {
            baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        var path = Path.Combine(baseFolder, AppFolderName, "data");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Detección heurística: si el proceso es ejecutado por SCM, no hay consola interactiva,
    /// el usuario es LocalSystem o NetworkService, o no hay sesión interactiva.
    /// </summary>
    public static bool IsRunningAsWindowsService()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return false;

            // Servicios no tienen sesión interactiva
            if (Environment.UserInteractive)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Migración one-shot: si hay datos legacy en LocalAppData y el destino del servicio
    /// (ProgramData) está vacío, se copian. No borra el original (rollback manual posible).
    /// </summary>
    public static void MigrateLegacyDataIfNeeded()
    {
        try
        {
            var dest = DataDirectory;
            if (!IsRunningAsWindowsService())
                return; // solo migra cuando arranca como servicio

            // Si ya hay BD en el destino, asumimos migrado
            if (File.Exists(Path.Combine(dest, "grunflex_api.db")))
                return;

            var legacyRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppFolderName, "data");

            if (!Directory.Exists(legacyRoot))
                return;

            foreach (var file in Directory.GetFiles(legacyRoot))
            {
                var destFile = Path.Combine(dest, Path.GetFileName(file));
                if (!File.Exists(destFile))
                    File.Copy(file, destFile, overwrite: false);
            }
        }
        catch
        {
            // sin bloquear arranque
        }
    }
}
