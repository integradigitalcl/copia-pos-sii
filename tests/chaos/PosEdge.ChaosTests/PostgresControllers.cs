using System.Diagnostics;

namespace PosEdge.ChaosTests;

public interface IPostgresController
{
    string Kind { get; }
    Task<bool> DetectAsync(CancellationToken ct);
    Task RestartAsync(CancellationToken ct);
}

public static class PostgresControllerFactory
{
    public static async Task<IPostgresController?> DetectAsync(CancellationToken ct)
    {
        var candidates = new IPostgresController[]
        {
            new WindowsServicePostgresController(),
            new PgCtlPostgresController(),
            new DockerPostgresController()
        };

        foreach (var c in candidates)
        {
            try
            {
                if (await c.DetectAsync(ct))
                    return c;
            }
            catch { }
        }
        return null;
    }
}

public sealed class WindowsServicePostgresController : IPostgresController
{
    public string Kind => "windows-service";

    private string? _serviceName;

    public async Task<bool> DetectAsync(CancellationToken ct)
    {
        // Common service name patterns: postgresql-x64-16, postgresql-x64-15, etc.
        var names = new[] { "postgresql-x64-16", "postgresql-x64-15", "postgresql-x64-14", "postgresql-x64-13", "postgresql-x64-12", "postgresql" };
        foreach (var n in names)
        {
            var ok = await RunScAsync($"query \"{n}\"", ct);
            if (ok.exitCode == 0)
            {
                _serviceName = n;
                return true;
            }
        }
        return false;
    }

    public async Task RestartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_serviceName))
            throw new InvalidOperationException("service not detected");

        await RunScAsync($"stop \"{_serviceName}\"", ct);
        await Task.Delay(1500, ct);
        await RunScAsync($"start \"{_serviceName}\"", ct);
        await Task.Delay(1500, ct);
    }

    private static Task<(int exitCode, string stdout, string stderr)> RunScAsync(string args, CancellationToken ct) =>
        Proc.RunProcessAsync("sc.exe", args, ct);
}

public sealed class PgCtlPostgresController : IPostgresController
{
    public string Kind => "pg_ctl";

    private string? _pgCtl;
    private string? _dataDir;

    public async Task<bool> DetectAsync(CancellationToken ct)
    {
        // If PGDATA is set and pg_ctl is on PATH, use it.
        _dataDir = Environment.GetEnvironmentVariable("PGDATA");
        if (string.IsNullOrWhiteSpace(_dataDir))
            return false;

        var ok = await Proc.RunProcessAsync("pg_ctl", "--version", ct);
        if (ok.exitCode != 0) return false;
        _pgCtl = "pg_ctl";
        return true;
    }

    public async Task RestartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_pgCtl) || string.IsNullOrWhiteSpace(_dataDir))
            throw new InvalidOperationException("pg_ctl not detected");

        await Proc.RunProcessAsync(_pgCtl, $"-D \"{_dataDir}\" restart -m fast -w -t 60", ct);
        await Task.Delay(1000, ct);
    }
}

public sealed class DockerPostgresController : IPostgresController
{
    public string Kind => "docker";

    private string? _containerId;

    public async Task<bool> DetectAsync(CancellationToken ct)
    {
        // If docker exists and a postgres container is running, pick the first.
        var v = await Proc.RunProcessAsync("docker", "version", ct);
        if (v.exitCode != 0) return false;

        var ps = await Proc.RunProcessAsync("docker", "ps --format \"{{.ID}} {{.Image}} {{.Names}}\"", ct);
        if (ps.exitCode != 0) return false;
        foreach (var line in ps.stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("postgres", StringComparison.OrdinalIgnoreCase))
            {
                _containerId = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                return !string.IsNullOrWhiteSpace(_containerId);
            }
        }
        return false;
    }

    public async Task RestartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_containerId))
            throw new InvalidOperationException("docker container not detected");

        await Proc.RunProcessAsync("docker", $"restart {_containerId}", ct);
        await Task.Delay(1500, ct);
    }
}

internal static class Proc
{
    public static async Task<(int exitCode, string stdout, string stderr)> RunProcessAsync(string fileName, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var p = new Process { StartInfo = psi };
        p.Start();
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }
}

