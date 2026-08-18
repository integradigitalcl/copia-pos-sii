using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace PosEdge.InstallerCore;

public static partial class InstallerCore
{
    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PosEdge", "config");

    public static void EnsureConfigDir() => Directory.CreateDirectory(ConfigDir);

    public static string EnsureMachineId()
    {
        EnsureConfigDir();
        var p = Path.Combine(ConfigDir, "machine-id.txt");
        if (File.Exists(p))
            return File.ReadAllText(p, Encoding.UTF8).Trim();

        var id = Guid.NewGuid().ToString("D");
        File.WriteAllText(p, id, Encoding.UTF8);
        return id;
    }

    public static async Task<bool> IsHealthyAsync(string apiBase, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = await http.GetAsync(apiBase.TrimEnd('/') + "/health/ready", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static Task<string?> RunDiscoveryAsync(string discoveryExePath, CancellationToken ct) =>
        RunDiscoveryAsync(discoveryExePath, 5071, ct);

    public static async Task<string?> RunDiscoveryAsync(string discoveryExePath, int port, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = discoveryExePath,
                Arguments = $"--prefer-udp --port {port} --timeout-ms 3500",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) return null;
            var s = output.Trim();
            if (string.IsNullOrWhiteSpace(s)) return null;
            return s.EndsWith("/") ? s : s + "/";
        }
        catch
        {
            return null;
        }
    }

    public static void EnsureFirewallRuleTcp5071()
    {
        RunNetsh("advfirewall firewall delete rule name=\"PosEdge API (5071)\"");
        RunNetsh("advfirewall firewall add rule name=\"PosEdge API (5071)\" dir=in action=allow protocol=TCP localport=5071 profile=any");
    }

    public static void EnsureFirewallRuleUdp33279()
    {
        RunNetsh("advfirewall firewall delete rule name=\"PosEdge Discovery (33279)\"");
        RunNetsh("advfirewall firewall add rule name=\"PosEdge Discovery (33279)\" dir=in action=allow protocol=UDP localport=33279 profile=any");
    }

    public static void EnsureFirewallRuleUdp5353()
    {
        RunNetsh("advfirewall firewall delete rule name=\"PosEdge mDNS (5353)\"");
        RunNetsh("advfirewall firewall add rule name=\"PosEdge mDNS (5353)\" dir=in action=allow protocol=UDP localport=5353 profile=any");
    }

    public static void InstallOrUpdateService(string serviceName, string exePath, bool startAfterInstall = true)
    {
        if (!File.Exists(exePath))
            return;

        var fullExe = Path.GetFullPath(exePath);
        var existingBin = TryQueryServiceBinPath(serviceName);
        if (existingBin != null && PathsEqual(existingBin, fullExe))
        {
            SetServiceEnvironmentVariable(serviceName, "ASPNETCORE_ENVIRONMENT", "Production");
            SetServiceEnvironmentVariable(serviceName, "DOTNET_ENVIRONMENT", "Production");
            if (startAfterInstall)
                TryStartService(serviceName);
            return;
        }

        RunSc($"stop {serviceName}");
        RunSc($"delete {serviceName}");
        RunSc($"create {serviceName} binPath= \"\\\"{fullExe}\\\"\" start= auto DisplayName= \"{serviceName}\"");
        SetServiceEnvironmentVariable(serviceName, "ASPNETCORE_ENVIRONMENT", "Production");
        SetServiceEnvironmentVariable(serviceName, "DOTNET_ENVIRONMENT", "Production");
        RunSc($"failure {serviceName} reset= 60 actions= restart/5000/restart/5000/restart/5000");
        if (startAfterInstall)
            RunSc($"start {serviceName}");
    }

    public static string? TryQueryServiceState(string serviceName)
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
            if (p == null)
                return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output.Trim();
        }
        catch
        {
            return null;
        }
    }

    public static string? TryReadTailOfNewestLog(string directory, string pattern, int maxLines = 40)
    {
        try
        {
            if (!Directory.Exists(directory))
                return null;

            var file = Directory.GetFiles(directory, pattern)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (file == null)
                return null;

            var lines = File.ReadLines(file.FullName).TakeLast(maxLines).ToArray();
            return string.Join(Environment.NewLine, lines);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryQueryServiceBinPath(string serviceName)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"qc \"{serviceName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (p == null)
                return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            const string prefix = "BINARY_PATH_NAME";
            foreach (var line in output.Split('\n', '\r'))
            {
                var t = line.Trim();
                if (!t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var idx = t.IndexOf(':', StringComparison.Ordinal);
                if (idx < 0)
                    return null;
                return t[(idx + 1)..].Trim().Trim('"');
            }
        }
        catch { }

        return null;
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a.Trim().Trim('"')),
                Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void LogSetupFailure(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(LogsDir);
            var path = Path.Combine(LogsDir, $"setup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, ex.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    private static void SetServiceEnvironmentVariable(string serviceName, string name, string value)
    {
        try
        {
            var key = $@"HKLM\SYSTEM\CurrentControlSet\Services\{serviceName}\Environment";
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = $"add \"{key}\" /v {name} /t REG_SZ /d {value} /f",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            p?.WaitForExit(15_000);
        }
        catch { }
    }

    public static void TryStartService(string serviceName)
    {
        RunSc($"start {serviceName}");
    }

    public static void TryStopService(string serviceName)
    {
        RunSc($"stop {serviceName}");
    }

    public static string NewRandomHex(int bytes)
    {
        Span<byte> b = stackalloc byte[bytes];
        RandomNumberGenerator.Fill(b);
        var sb = new StringBuilder(bytes * 2);
        foreach (var x in b)
            sb.Append(x.ToString("x2"));
        return sb.ToString();
    }

    private static void RunNetsh(string argsLine)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = argsLine,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            p?.WaitForExit(15_000);
        }
        catch { }
    }

    private static void RunSc(string argsLine)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = argsLine,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            p?.WaitForExit(15_000);
        }
        catch { }
    }
}
