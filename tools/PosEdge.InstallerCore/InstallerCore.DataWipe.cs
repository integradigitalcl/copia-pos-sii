using System.Diagnostics;
using System.Text;

namespace PosEdge.InstallerCore;

public static partial class InstallerCore
{
    public const string InstallWipeDataFlagFileName = "install-wipe-data.flag";

    /// <summary>
    /// Borra datos locales de Grunflex POS y reinicia configuración PosEdge para una instalación limpia.
    /// No desinstala PostgreSQL ni los servicios; recrea BD PosEdge y SQLite al provisionar.
    /// </summary>
    public static void WipeApplicationDataForCleanInstall(string? payloadRoot = null)
    {
        LogSetupMessage("Inicio borrado de datos (instalación limpia).");

        TryKillProcess("GrunflexPOS2");
        TryKillProcess("GrunflexPOS.API");

        foreach (var svc in new[]
                 {
                     "GrunflexPOSAPI",
                     "PosEdgeGuardian",
                     "PosEdgeWorkers",
                     "PosEdgeApi"
                 })
        {
            TryStopService(svc);
        }

        Thread.Sleep(1500);

        var grunflexPd = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GrunflexPOS");
        var grunflexLa = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GrunflexPOS");

        TryDeleteDirectory(grunflexPd);
        TryDeleteDirectory(grunflexLa);

        TryDeletePosEdgeRoleAndCacheConfig();
        TryWipePosEdgeDatabase();

        var backups = Path.Combine(ProgramDataRoot, "backups");
        TryDeleteDirectory(backups);

        LogSetupMessage("Borrado de datos completado.");
    }

    public static bool IsInstallWipeDataRequested()
    {
        try
        {
            var path = Path.Combine(ConfigDir, InstallWipeDataFlagFileName);
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public static void ClearInstallWipeDataFlag()
    {
        try
        {
            var path = Path.Combine(ConfigDir, InstallWipeDataFlagFileName);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static void TryDeletePosEdgeRoleAndCacheConfig()
    {
        EnsureConfigDir();
        foreach (var name in new[]
                 {
                     "role.txt",
                     "cached-server.txt",
                     "primary-server.lock",
                     InstallWipeDataFlagFileName
                 })
        {
            TryDeleteFile(Path.Combine(ConfigDir, name));
        }

        TryDeleteFile(Path.Combine(TrustDir, "trusted-authority.json"));
    }

    private static void TryWipePosEdgeDatabase()
    {
        var secrets = TryGetServerSecrets();
        if (secrets == null)
            return;

        if (!CanConnectAsPostgres(secrets.PgPort, secrets.PgSuperPwd))
            return;

        var psql = FindPosEdgePsqlExe();
        if (psql == null)
            return;

        try
        {
            ExecPsql(psql, secrets.PgPort, "postgres", secrets.PgSuperPwd,
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'posedgedb' AND pid <> pg_backend_pid();");
        }
        catch { }

        try
        {
            ExecPsql(psql, secrets.PgPort, "postgres", secrets.PgSuperPwd,
                "DROP DATABASE IF EXISTS posedgedb;");
        }
        catch (Exception ex)
        {
            LogSetupFailure(ex);
        }

        try
        {
            ExecPsql(psql, secrets.PgPort, "postgres", secrets.PgSuperPwd,
                "DROP ROLE IF EXISTS posedgedb_user;");
        }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                LogSetupMessage($"Eliminado: {path}");
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 2)
                    LogSetupFailure(ex);
                Thread.Sleep(500);
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static void TryKillProcess(string processName)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (!p.HasExited)
                        p.Kill(entireProcessTree: true);
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch { }
    }

    private static void LogSetupMessage(string message)
    {
        try
        {
            Directory.CreateDirectory(LogsDir);
            var path = Path.Combine(LogsDir, "setup-wipe.log");
            File.AppendAllText(path, $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }
}
