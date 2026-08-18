using PosEdge.InstallerCore;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

// Creates a backup bundle: pg_dump + config/cluster/trust snapshots.
// Exit codes: 0 success, 20 failure

try
{
    var kind = (ReadArg("--kind") ?? "daily").Trim().ToLowerInvariant(); // daily|weekly|preupdate
    var root = Path.Combine(InstallerCore.ProgramDataRoot, "backups");
    Directory.CreateDirectory(root);

    var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
    var dir = Path.Combine(root, $"{kind}-{stamp}");
    Directory.CreateDirectory(dir);

    var secretsPath = Path.Combine(InstallerCore.ConfigDir, "server-secrets.json");
    var secrets = ReadJson<ServerSecretsDto>(secretsPath);
    if (secrets == null) throw new Exception("Missing secrets.");

    // pg_dump
    var dumpPath = Path.Combine(dir, "posedgedb.dump");
    RunPgDump(secrets.PgPort, secrets.DbPwd, dumpPath);

    // snapshots
    CopyDirIfExists(InstallerCore.ConfigDir, Path.Combine(dir, "config"));
    CopyDirIfExists(Path.Combine(InstallerCore.ProgramDataRoot, "cluster"), Path.Combine(dir, "cluster"));
    CopyDirIfExists(Path.Combine(InstallerCore.ProgramDataRoot, "trust"), Path.Combine(dir, "trust"));

    // zip bundle
    var zip = dir + ".zip";
    if (File.Exists(zip)) File.Delete(zip);
    ZipFile.CreateFromDirectory(dir, zip, CompressionLevel.Optimal, includeBaseDirectory: false);

    // retention (keep last N)
    ApplyRetention(root, kind, keep: kind == "weekly" ? 8 : 14);

    Console.WriteLine(zip);
    return 0;
}
catch
{
    return 20;
}

static string? ReadArg(string key)
{
    var args = Environment.GetCommandLineArgs();
    for (var i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    return null;
}

static T? ReadJson<T>(string path) where T : class
{
    try
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch { return null; }
}

static void RunPgDump(int port, string dbPwd, string outPath)
{
    // Use pg_dump from installed Postgres (Program Files).
    var pgDump = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL", "17", "bin", "pg_dump.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL", "16", "bin", "pg_dump.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL", "15", "bin", "pg_dump.exe"),
    }.FirstOrDefault(File.Exists);

    if (pgDump == null) throw new Exception("pg_dump not found.");

    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = pgDump,
        Arguments = $"-h 127.0.0.1 -p \"{port}\" -U posedgedb_user -F c -f \"{outPath}\" posedgedb",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    psi.Environment["PGPASSWORD"] = dbPwd;
    using var p = System.Diagnostics.Process.Start(psi);
    if (p == null) throw new Exception("pg_dump failed.");
    p.WaitForExit(10 * 60_000);
    if (p.ExitCode != 0) throw new Exception("pg_dump failed.");
}

static void CopyDirIfExists(string srcDir, string destDir)
{
    if (!Directory.Exists(srcDir)) return;
    Directory.CreateDirectory(destDir);
    foreach (var dirPath in Directory.GetDirectories(srcDir, "*", SearchOption.AllDirectories))
        Directory.CreateDirectory(dirPath.Replace(srcDir, destDir));
    foreach (var filePath in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
    {
        var dest = filePath.Replace(srcDir, destDir);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(filePath, dest, overwrite: true);
    }
}

static void ApplyRetention(string root, string kind, int keep)
{
    try
    {
        var zips = Directory.GetFiles(root, $"{kind}-*.zip", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var f in zips.Skip(keep))
        {
            try { File.Delete(f); } catch { }
        }
    }
    catch { }
}

file sealed class ServerSecretsDto
{
    public int PgPort { get; set; }
    public string PgSuperPwd { get; set; } = "";
    public string DbPwd { get; set; } = "";
    public string OpsKey { get; set; } = "";
    public string PgService { get; set; } = "";
}
