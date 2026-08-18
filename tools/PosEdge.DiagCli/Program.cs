using PosEdge.InstallerCore;
using System.IO.Compression;
using System.Text;

try
{
    var outPath = ReadArg("--out") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        $"PosEdge-Diag-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");

    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

    var tmp = Path.Combine(Path.GetTempPath(), "PosEdgeDiag_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);

    WriteText(Path.Combine(tmp, "summary.txt"), BuildSummary());
    CopyIfExists(InstallerCore.ConfigDir, Path.Combine(tmp, "ProgramData-Config"));
    CopyIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PosEdge", "cluster"), Path.Combine(tmp, "ProgramData-Cluster"));
    CopyIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PosEdge", "logs"), Path.Combine(tmp, "ProgramData-Logs"));

    if (File.Exists(outPath)) File.Delete(outPath);
    ZipFile.CreateFromDirectory(tmp, outPath, CompressionLevel.Optimal, includeBaseDirectory: false);

    try { Directory.Delete(tmp, recursive: true); } catch { }

    Console.WriteLine(outPath);
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

static void WriteText(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content, Encoding.UTF8);
}

static void CopyIfExists(string srcDir, string destDir)
{
    try
    {
        if (!Directory.Exists(srcDir)) return;
        foreach (var dirPath in Directory.GetDirectories(srcDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dirPath.Replace(srcDir, destDir));
        foreach (var filePath in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            var dest = filePath.Replace(srcDir, destDir);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(filePath, dest, overwrite: true);
        }
    }
    catch { }
}

static string BuildSummary()
{
    var sb = new StringBuilder();
    sb.AppendLine("PosEdge Diagnostic Bundle");
    sb.AppendLine($"CollectedAt: {DateTimeOffset.Now:O}");
    sb.AppendLine($"Machine: {Environment.MachineName}");
    sb.AppendLine($"OS: {Environment.OSVersion}");
    sb.AppendLine();
    sb.AppendLine("Services:");
    sb.AppendLine($"- PosEdgeApi: {SvcState("PosEdgeApi")}");
    sb.AppendLine($"- PosEdgeWorkers: {SvcState("PosEdgeWorkers")}");
    sb.AppendLine($"- PosEdgeGuardian: {SvcState("PosEdgeGuardian")}");
    return sb.ToString();
}

static string SvcState(string name)
{
    try
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = $"query {name}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        if (p == null) return "unknown";
        var t = p.StandardOutput.ReadToEnd();
        p.WaitForExit(3000);
        if (t.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) return "running";
        if (t.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return "stopped";
        return "unknown";
    }
    catch { return "unknown"; }
}
