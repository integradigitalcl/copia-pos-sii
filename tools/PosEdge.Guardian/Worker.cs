using System.Net.NetworkInformation;
using System.Text;

namespace PosEdge.Guardian;

public sealed class Worker(ILogger<Worker> log) : BackgroundService
{
    // Keep CPU low: one loop, bounded work, small timeouts.
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(10);

    // Anti-restart loops
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private DateTimeOffset _nextDailyBackupAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextWeeklyBackupAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        global::PosEdge.InstallerCore.InstallerCore.EnsureConfigDir();
        Directory.CreateDirectory(global::PosEdge.InstallerCore.InstallerCore.LogsDir);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                if (log.IsEnabled(LogLevel.Warning))
                    log.LogWarning(ex, "Guardian tick failed (failures={Failures})", _consecutiveFailures);
            }

            try { await Task.Delay(_interval, stoppingToken).ConfigureAwait(false); } catch { }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _cooldownUntil)
            return;

        var role = SafeRead(Path.Combine(global::PosEdge.InstallerCore.InstallerCore.ConfigDir, "role.txt"));
        if (string.Equals(role, "server", StringComparison.OrdinalIgnoreCase))
        {
            await TickServerAsync(ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(role, "terminal", StringComparison.OrdinalIgnoreCase))
        {
            await TickTerminalAsync(ct).ConfigureAwait(false);
            return;
        }
    }

    private async Task TickServerAsync(CancellationToken ct)
    {
        // 1) Ensure firewall rules
        global::PosEdge.InstallerCore.InstallerCore.EnsureFirewallRuleTcp5071();
        global::PosEdge.InstallerCore.InstallerCore.EnsureFirewallRuleUdp33279();
        global::PosEdge.InstallerCore.InstallerCore.EnsureFirewallRuleUdp5353();

        // 2) Disk free space (degraded protection)
        if (IsLowDisk())
        {
            EnterCooldown(seconds: 60);
            return;
        }

        // 3) Ensure services are running
        EnsureRunning("PosEdgePostgres", softOnly: true); // may be external install; just start if present
        EnsureRunning("PosEdgeApi");
        EnsureRunning("PosEdgeWorkers");
        EnsureRunning("PosEdgeGuardian", softOnly: true);

        // 4) Health check API; if not ready, restart API+Workers with cooldown
        var ok = await global::PosEdge.InstallerCore.InstallerCore.IsHealthyAsync("http://127.0.0.1:5071/", ct).ConfigureAwait(false);
        if (!ok)
        {
            Restart("PosEdgeApi");
            Restart("PosEdgeWorkers");
            EnterCooldown(seconds: Math.Min(120, 10 * Math.Max(1, _consecutiveFailures)));
            return;
        }

        // 5) Backups (lightweight scheduling)
        await TryRunBackupsAsync(ct).ConfigureAwait(false);
    }

    private async Task TickTerminalAsync(CancellationToken ct)
    {
        var payloadRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")); // {app}\Guardian\ -> {app}\
        var cached = SafeRead(Path.Combine(global::PosEdge.InstallerCore.InstallerCore.ConfigDir, "cached-server.txt"));

        // Prefer cached if healthy and authorized.
        if (!string.IsNullOrWhiteSpace(cached))
        {
            var api = NormalizeBase(cached);
            if (api != null && await global::PosEdge.InstallerCore.InstallerCore.IsHealthyAsync(api, ct).ConfigureAwait(false))
            {
                var auth = await global::PosEdge.InstallerCore.InstallerCore.FetchAndValidateAuthorityAsync(api, ct).ConfigureAwait(false);
                if (auth != null && IsTrusted(auth))
                {
                    RewriteTerminalConfigIfNeeded(payloadRoot, api);
                    return;
                }
            }
        }

        // Rediscover: UDP -> subnet (mDNS added next iteration)
        var discoveryExe = Path.Combine(payloadRoot, "DiscoveryCli", "PosEdge.DiscoveryCli.exe");
        var apiBase = File.Exists(discoveryExe) ? await global::PosEdge.InstallerCore.InstallerCore.RunDiscoveryAsync(discoveryExe, ct).ConfigureAwait(false) : null;
        if (apiBase == null) return;

        var authority = await global::PosEdge.InstallerCore.InstallerCore.FetchAndValidateAuthorityAsync(apiBase, ct).ConfigureAwait(false);
        if (authority == null) return;
        if (!IsTrusted(authority)) return;

        File.WriteAllText(Path.Combine(global::PosEdge.InstallerCore.InstallerCore.ConfigDir, "cached-server.txt"), apiBase, Encoding.UTF8);
        RewriteTerminalConfigIfNeeded(payloadRoot, apiBase);
    }

    private static void RewriteTerminalConfigIfNeeded(string payloadRoot, string apiBase)
    {
        try
        {
            var terminalDir = Path.Combine(payloadRoot, "Terminal");
            var cfgPath = Path.Combine(terminalDir, "appsettings.Production.json");
            if (!Directory.Exists(terminalDir) || !File.Exists(cfgPath)) return;

            var text = File.ReadAllText(cfgPath, Encoding.UTF8);
            if (text.Contains(apiBase, StringComparison.OrdinalIgnoreCase))
                return;

            // Minimal rewrite: replace BaseUrl line if present; otherwise do nothing.
            var replaced = ReplaceBaseUrl(text, apiBase);
            if (replaced != null)
                File.WriteAllText(cfgPath, replaced, Encoding.UTF8);
        }
        catch { }
    }

    private static string? ReplaceBaseUrl(string json, string apiBase)
    {
        var marker = "\"BaseUrl\"";
        var idx = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        // very small/robust replacement: find next ':' and next quoted string.
        var colon = json.IndexOf(':', idx);
        if (colon < 0) return null;
        var q1 = json.IndexOf('"', colon + 1);
        if (q1 < 0) return null;
        var q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0) return null;
        return json[..(q1 + 1)] + apiBase + json[q2..];
    }

    private bool IsTrusted(global::PosEdge.InstallerCore.TrustedAuthority authority)
    {
        var trusted = global::PosEdge.InstallerCore.InstallerCore.ReadTrustedAuthority();
        if (trusted == null)
        {
            global::PosEdge.InstallerCore.InstallerCore.PersistTrustedAuthority(authority);
            return true;
        }
        return string.Equals(trusted.Fingerprint, authority.Fingerprint, StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureRunning(string svc, bool softOnly = false)
    {
        try
        {
            global::PosEdge.InstallerCore.InstallerCore.TryStartService(svc);
        }
        catch
        {
            if (!softOnly) throw;
        }
    }

    private void Restart(string svc)
    {
        global::PosEdge.InstallerCore.InstallerCore.TryStopService(svc);
        global::PosEdge.InstallerCore.InstallerCore.TryStartService(svc);
    }

    private void EnterCooldown(int seconds)
    {
        _cooldownUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
    }

    private async Task TryRunBackupsAsync(CancellationToken ct)
    {
        // Schedule once per day and once per week, but only when server is healthy.
        var now = DateTimeOffset.Now;
        if (_nextDailyBackupAt == DateTimeOffset.MinValue)
            _nextDailyBackupAt = now.Date.AddHours(3).AddMinutes(5); // 03:05 local
        if (_nextWeeklyBackupAt == DateTimeOffset.MinValue)
            _nextWeeklyBackupAt = NextWeekly(now, DayOfWeek.Sunday, 3, 25);

        if (now >= _nextDailyBackupAt)
        {
            await RunBackupCliAsync("daily", ct).ConfigureAwait(false);
            _nextDailyBackupAt = _nextDailyBackupAt.AddDays(1);
        }

        if (now >= _nextWeeklyBackupAt)
        {
            await RunBackupCliAsync("weekly", ct).ConfigureAwait(false);
            _nextWeeklyBackupAt = NextWeekly(now.AddDays(1), DayOfWeek.Sunday, 3, 25);
        }
    }

    private static DateTimeOffset NextWeekly(DateTimeOffset now, DayOfWeek day, int hour, int minute)
    {
        var d = now.Date;
        while (d.DayOfWeek != day) d = d.AddDays(1);
        return new DateTimeOffset(d.AddHours(hour).AddMinutes(minute), now.Offset);
    }

    private static async Task RunBackupCliAsync(string kind, CancellationToken ct)
    {
        try
        {
            var appRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            var exe = Path.Combine(appRoot, "BackupCli", "PosEdge.BackupCli.exe");
            if (!File.Exists(exe)) return;

            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--kind {kind}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (p == null) return;
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch { }
    }

    private static bool IsLowDisk()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(global::PosEdge.InstallerCore.InstallerCore.ProgramDataRoot)!);
            if (!drive.IsReady) return false;
            // Degrade if < 1GB free.
            return drive.AvailableFreeSpace < 1_000_000_000L;
        }
        catch { return false; }
    }

    private static string? SafeRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path, Encoding.UTF8).Trim();
        }
        catch { return null; }
    }

    private static string? NormalizeBase(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (!s.EndsWith("/")) s += "/";
        return s;
    }
}
