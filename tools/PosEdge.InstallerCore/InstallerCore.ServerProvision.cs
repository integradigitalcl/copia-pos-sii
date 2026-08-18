using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PosEdge.InstallerCore;

public static partial class InstallerCore
{
    public sealed record ServerSecrets(string PgService, int PgPort, string PgSuperPwd, string DbPwd, string OpsKey);

    public static string ProgramDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PosEdge");

    public static string ClusterDir => Path.Combine(ProgramDataRoot, "cluster");
    public static string LogsDir => Path.Combine(ProgramDataRoot, "logs");

    public static string EnsureClusterIdentity()
    {
        Directory.CreateDirectory(ClusterDir);

        var clusterIdPath = Path.Combine(ClusterDir, "cluster-id.txt");
        var primaryIdPath = Path.Combine(ClusterDir, "primary-server-id.txt");

        if (!File.Exists(clusterIdPath))
            File.WriteAllText(clusterIdPath, Guid.NewGuid().ToString("D"), Encoding.UTF8);

        if (!File.Exists(primaryIdPath))
            File.WriteAllText(primaryIdPath, Guid.NewGuid().ToString("D"), Encoding.UTF8);

        return File.ReadAllText(clusterIdPath, Encoding.UTF8).Trim();
    }

    public static ServerSecrets? TryGetServerSecrets()
    {
        var secretsPath = Path.Combine(ConfigDir, "server-secrets.json");
        return TryReadSecrets(secretsPath);
    }

    /// <summary>
    /// Runtime path for launcher: skips full install when secrets already exist (post-setup).
    /// </summary>
    public static void EnsureServerRuntimeReady(string payloadRoot, int postgresWaitSeconds = 45)
    {
        try
        {
            var secrets = TryGetServerSecrets();
            if (secrets == null)
            {
                try { EnsureServerProvisioned(payloadRoot); }
                catch (Exception ex) { LogSetupFailure(ex); }
                return;
            }

            if (!TryWaitForPostgresReady(secrets.PgPort, secrets.PgSuperPwd, postgresWaitSeconds * 1000))
            {
                LogSetupFailure(new TimeoutException("PostgreSQL no respondió (runtime)."));
                return;
            }

            try
            {
                if (!IsDatabaseProvisioned(secrets.PgPort, secrets.PgSuperPwd))
                    BootstrapDb(payloadRoot, secrets.PgPort, secrets.PgSuperPwd, secrets.DbPwd);
            }
            catch (Exception ex) { LogSetupFailure(ex); }

            WriteServerAppSettings(payloadRoot, secrets.PgPort, secrets.DbPwd, secrets.OpsKey);
        }
        catch (Exception ex)
        {
            LogSetupFailure(ex);
        }
    }

    private static bool TryWaitForPostgresReady(int pgPort, string pgSuperPwd, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (CanConnectAsPostgres(pgPort, pgSuperPwd))
                return true;
            Thread.Sleep(1000);
        }

        return false;
    }

    public static ServerSecrets EnsureServerProvisioned(string payloadRoot)
    {
        EnsureConfigDir();
        Directory.CreateDirectory(LogsDir);
        _ = EnsureMachineId();
        _ = EnsureClusterIdentity();

        var secretsPath = Path.Combine(ConfigDir, "server-secrets.json");
        var existing = TryReadSecrets(secretsPath);

        var pgSvc = "PosEdgePostgres";
        var pgPort = existing?.PgPort ?? ResolvePostgresPort(pgSvc);
        var pgSuperPwd = existing?.PgSuperPwd ?? NewRandomHex(12);
        var dbPwd = existing?.DbPwd ?? NewRandomHex(12);
        var opsKey = existing?.OpsKey ?? NewRandomHex(16);

        InstallPostgresIfMissing(payloadRoot, pgSvc, pgPort, pgSuperPwd);
        if (!TryWaitForPostgresReady(pgPort, pgSuperPwd, 120_000))
            throw new TimeoutException("PostgreSQL no respondió a tiempo.");

        if (!IsDatabaseProvisioned(pgPort, pgSuperPwd))
            BootstrapDb(payloadRoot, pgPort, pgSuperPwd, dbPwd);

        WriteServerAppSettings(payloadRoot, pgPort, dbPwd, opsKey);

        var secrets = new ServerSecrets(pgSvc, pgPort, pgSuperPwd, dbPwd, opsKey);
        WriteJson(secretsPath, secrets);
        return secrets;
    }

    private static ServerSecrets? TryReadSecrets(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ServerSecrets>(File.ReadAllText(path, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    private static void WriteJson<T>(string path, T obj)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }

    private static int ResolvePostgresPort(string pgSvc)
    {
        var fromData = ReadPortFromPosEdgeDataDir();
        if (fromData.HasValue)
            return fromData.Value;

        if (ServiceExists(pgSvc))
            return ReadPortFromPosEdgeDataDir() ?? 5432;

        return PickAvailablePostgresPort();
    }

    private static int PickAvailablePostgresPort()
    {
        for (var port = 5432; port <= 5440; port++)
        {
            if (!IsLocalTcpPortInUse(port))
                return port;
        }

        throw new InvalidOperationException(
            "Puerto PostgreSQL no disponible (5432-5440 ocupados). Cierre otras instancias de PostgreSQL e intente de nuevo.");
    }

    private static bool IsLocalTcpPortInUse(int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync("127.0.0.1", port);
            if (!connect.Wait(TimeSpan.FromMilliseconds(400)))
                return false;
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static int? ReadPortFromPosEdgeDataDir()
    {
        var conf = Path.Combine(ProgramDataRoot, "postgres", "data", "postgresql.conf");
        if (!File.Exists(conf))
            return null;

        foreach (var line in File.ReadLines(conf))
        {
            var m = Regex.Match(line.Trim(), @"^port\s*=\s*(\d+)", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var p))
                return p;
        }

        return null;
    }

    private static void InstallPostgresIfMissing(string payloadRoot, string pgSvc, int pgPort, string pgSuperPwd)
    {
        if (ServiceExists(pgSvc))
            return;

        var installerExe = Path.Combine(payloadRoot, "Prerequisites", "postgresql-windows-x64.exe");
        var prefix = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL", "15");
        var dataDir = Path.Combine(ProgramDataRoot, "postgres", "data");
        Directory.CreateDirectory(Path.GetDirectoryName(dataDir)!);

        // EDB installer unattended args (same intent as previous PS).
        var args = string.Join(' ', new[]
        {
            "--mode unattended",
            "--unattendedmodeui minimal",
            "--superaccount postgres",
            $"--superpassword \"{pgSuperPwd}\"",
            $"--serverport \"{pgPort}\"",
            $"--servicename \"{pgSvc}\"",
            $"--prefix \"{prefix}\"",
            $"--datadir \"{dataDir}\"",
            "--enable_acledit 1",
            "--disable-components pgAdmin,stackbuilder"
        });

        RunAndWait(installerExe, args, timeoutMs: 8 * 60_000);
    }

    private static void WaitForPostgresReady(int pgPort, string pgSuperPwd, int timeoutMs = 120_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (CanConnectAsPostgres(pgPort, pgSuperPwd))
                return;
            Thread.Sleep(1000);
        }

        throw new TimeoutException("PostgreSQL no respondió a tiempo.");
    }

    private static bool CanConnectAsPostgres(int pgPort, string pgSuperPwd)
    {
        var psql = FindPosEdgePsqlExe();
        if (psql == null)
            return false;

        try
        {
            return RunPsqlQuery(psql, pgPort, "postgres", pgSuperPwd, "SELECT 1") == "1";
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDatabaseProvisioned(int pgPort, string pgSuperPwd)
    {
        var psql = FindPosEdgePsqlExe();
        if (psql == null)
            return false;

        try
        {
            return RunPsqlQuery(psql, pgPort, "postgres", pgSuperPwd,
                "SELECT 1 FROM pg_roles WHERE rolname = 'posedgedb_user'") == "1";
        }
        catch
        {
            return false;
        }
    }

    private static void BootstrapDb(string payloadRoot, int pgPort, string pgSuperPwd, string dbPwd)
    {
        var psql = FindPosEdgePsqlExe();
        if (psql == null)
            throw new InvalidOperationException("PostgreSQL de PosEdge no disponible.");

        ExecPsql(psql, pgPort, "postgres", pgSuperPwd,
            "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'posedgedb') THEN CREATE DATABASE posedgedb; END IF; END $$;");
        ExecPsql(psql, pgPort, "postgres", pgSuperPwd,
            $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'posedgedb_user') THEN CREATE ROLE posedgedb_user LOGIN PASSWORD '{dbPwd}'; END IF; END $$;");
        ExecPsql(psql, pgPort, "postgres", pgSuperPwd, "GRANT ALL PRIVILEGES ON DATABASE posedgedb TO posedgedb_user;");

        var schema = Path.Combine(payloadRoot, "SetupExtras", "schema.sql");
        var seed = Path.Combine(payloadRoot, "SetupExtras", "seed.sql");
        if (File.Exists(schema)) ExecPsqlFile(psql, pgPort, "posedgedb", pgSuperPwd, schema);
        if (File.Exists(seed)) ExecPsqlFile(psql, pgPort, "posedgedb", pgSuperPwd, seed);
    }

    private static void WriteServerAppSettings(string payloadRoot, int pgPort, string dbPwd, string opsKey)
    {
        var conn = $"Host=127.0.0.1;Port={pgPort};Database=posedgedb;Username=posedgedb_user;Password={dbPwd};Pooling=true;Maximum Pool Size=50";
        var apiDir = Path.Combine(payloadRoot, "Api");
        var workersDir = Path.Combine(payloadRoot, "Workers");
        Directory.CreateDirectory(apiDir);
        Directory.CreateDirectory(workersDir);

        var json = $$"""
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
    }
  },
  "Edge": {
    "AutoApplySchema": false,
    "SchemaPath": "",
    "AutoSeed": false,
    "SeedPath": "",
    "OpsKey": "{{opsKey}}"
  },
  "ConnectionStrings": {
    "PosEdge": "{{conn}}"
  },
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:5071"
      }
    }
  },
  "AllowedHosts": "*"
}
""";

        File.WriteAllText(Path.Combine(apiDir, "appsettings.Production.json"), json, Encoding.UTF8);
        File.WriteAllText(Path.Combine(workersDir, "appsettings.Production.json"), json, Encoding.UTF8);
        File.WriteAllText(Path.Combine(apiDir, "appsettings.json"), json, Encoding.UTF8);
        File.WriteAllText(Path.Combine(workersDir, "appsettings.json"), json, Encoding.UTF8);
    }

    private static string PosEdgePgPrefix =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL", "15");

    private static string? FindPosEdgePsqlExe()
    {
        var psql = Path.Combine(PosEdgePgPrefix, "bin", "psql.exe");
        return File.Exists(psql) ? psql : null;
    }

    private static void ExecPsql(string psqlExe, int port, string db, string superPwd, string sql)
    {
        _ = RunPsql(psqlExe, port, db, superPwd, "-v", "ON_ERROR_STOP=1", "-c", sql);
    }

    private static void ExecPsqlFile(string psqlExe, int port, string db, string superPwd, string filePath)
    {
        _ = RunPsql(psqlExe, port, db, superPwd, "-v", "ON_ERROR_STOP=1", "-f", filePath);
    }

    private static string RunPsqlQuery(string psqlExe, int port, string db, string superPwd, string sql)
    {
        var output = RunPsql(psqlExe, port, db, superPwd, "-tAc", sql);
        return output.Trim();
    }

    private static string RunPsql(string psqlExe, int port, string db, string superPwd, params string[] extraArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = psqlExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-h");
        psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("-U");
        psi.ArgumentList.Add("postgres");
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(db);
        foreach (var arg in extraArgs)
            psi.ArgumentList.Add(arg);

        psi.Environment["PGPASSWORD"] = superPwd;

        using var p = Process.Start(psi);
        if (p == null)
            throw new InvalidOperationException("psql failed to start.");

        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(10 * 60_000);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? $"psql exit code {p.ExitCode}"
                : stderr.Trim());
        }

        return stdout;
    }

    private static void RunAndWait(string file, string args, int timeoutMs)
    {
        if (!File.Exists(file))
            throw new FileNotFoundException("Prerequisito faltante.", file);

        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });

        if (p == null) throw new InvalidOperationException("Installer failed to start.");
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Installer timeout.");
        }
        if (p.ExitCode != 0)
            throw new InvalidOperationException("PostgreSQL install failed.");
    }

    private static bool ServiceExists(string serviceName)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"query \"{serviceName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (p == null) return false;
            p.WaitForExit(3000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}

