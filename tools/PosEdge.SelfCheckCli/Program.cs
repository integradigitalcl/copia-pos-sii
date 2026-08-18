using PosEdge.InstallerCore;
using System.Net.Http;
using System.Text;

// Outputs one of: PASS / DEGRADED / FAIL
// Exit codes: 0 PASS, 10 DEGRADED, 20 FAIL

var role = ReadText(Path.Combine(InstallerCore.ConfigDir, "role.txt"))?.Trim().ToLowerInvariant();
var issues = new List<string>();
var degraded = new List<string>();

// Common checks
if (!HasFirewallRule("PosEdge API (5071)")) degraded.Add("Firewall rule missing: API");
if (!HasFirewallRule("PosEdge Discovery (33279)")) degraded.Add("Firewall rule missing: UDP discovery");
if (!HasFirewallRule("PosEdge mDNS (5353)")) degraded.Add("Firewall rule missing: mDNS");

if (string.Equals(role, "server", StringComparison.OrdinalIgnoreCase))
{
    if (!IsServiceRunning("PosEdgeApi")) issues.Add("Service not running: PosEdgeApi");
    if (!IsServiceRunning("PosEdgeWorkers")) issues.Add("Service not running: PosEdgeWorkers");
    if (!IsServiceRunning("PosEdgeGuardian")) degraded.Add("Service not running: PosEdgeGuardian");
    if (!IsServicePresent("PosEdgePostgres")) degraded.Add("PostgreSQL service not found: PosEdgePostgres");

    if (!await IsOkAsync("http://127.0.0.1:5071/health/ready")) issues.Add("API not ready");
    if (!await IsOkAsync("http://127.0.0.1:5071/v1/cluster/identity")) issues.Add("Authority endpoint unavailable");
}
else if (string.Equals(role, "terminal", StringComparison.OrdinalIgnoreCase))
{
    var trusted = InstallerCore.ReadTrustedAuthority();
    if (trusted == null) degraded.Add("No trusted authority");

    var cached = ReadText(Path.Combine(InstallerCore.ConfigDir, "cached-server.txt"))?.Trim();
    if (string.IsNullOrWhiteSpace(cached)) degraded.Add("No cached server");
}
else
{
    degraded.Add("Role not set");
}

if (issues.Count == 0 && degraded.Count == 0)
{
    Console.WriteLine("PASS");
    return 0;
}

if (issues.Count == 0)
{
    Console.WriteLine("DEGRADED");
    return 10;
}

Console.WriteLine("FAIL");
return 20;

static string? ReadText(string path)
{
    try
    {
        if (!File.Exists(path)) return null;
        return File.ReadAllText(path, Encoding.UTF8);
    }
    catch { return null; }
}

static bool HasFirewallRule(string ruleName)
{
    try
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = $"advfirewall firewall show rule name=\"{ruleName}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        if (p == null) return false;
        var t = p.StandardOutput.ReadToEnd();
        p.WaitForExit(3000);
        return t.Contains("Rule Name", StringComparison.OrdinalIgnoreCase);
    }
    catch { return false; }
}

static bool IsServicePresent(string name)
{
    try
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = $"query \"{name}\"",
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

static bool IsServiceRunning(string name)
{
    try
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = $"query \"{name}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        if (p == null) return false;
        var t = p.StandardOutput.ReadToEnd();
        p.WaitForExit(3000);
        return t.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }
    catch { return false; }
}

static async Task<bool> IsOkAsync(string url)
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var resp = await http.GetAsync(url);
        return resp.IsSuccessStatusCode;
    }
    catch { return false; }
}
