using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace PosEdge.IntegrationTests;

public sealed class ProcessHarness : IAsyncDisposable
{
    private readonly List<Process> _procs = new();
    private readonly List<Task> _pumpTasks = new();

    public async Task<Process> StartDotnetAsync(string workingDir, string args, Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDir,
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (env != null)
        {
            foreach (var (k, v) in env)
                psi.Environment[k] = v;
        }

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Start();
        _procs.Add(p);

        // Drain output to avoid deadlocks and to help diagnose chaos flakiness.
        _pumpTasks.Add(PumpAsync(p.StandardOutput, $"[{Path.GetFileName(workingDir)}:out] "));
        _pumpTasks.Add(PumpAsync(p.StandardError, $"[{Path.GetFileName(workingDir)}:err] "));

        // Small delay so process can bind ports.
        await Task.Delay(500);
        return p;
    }

    public static int GetFreeTcpPort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static string FindRepoRoot(string startDir)
    {
        var d = new DirectoryInfo(startDir);
        while (d != null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "src")) &&
                File.Exists(Path.Combine(d.FullName, "src", "PosEdge.slnx")))
                return d.FullName;
            d = d.Parent;
        }
        throw new InvalidOperationException("Repo root not found from " + startDir);
    }

    public async Task WaitForHttpOkAsync(string baseUrl, string path, TimeSpan timeout)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var sw = Stopwatch.StartNew();
        Exception? last = null;
        while (sw.Elapsed < timeout)
        {
            try
            {
                var resp = await http.GetAsync(path);
                if (resp.IsSuccessStatusCode) return;
            }
            catch (Exception ex) { last = ex; }
            await Task.Delay(200);
        }
        throw new TimeoutException($"HTTP not ready {baseUrl}{path}. Last={last?.Message}");
    }

    public static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited)
                p.Kill(entireProcessTree: true);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _procs)
            Kill(p);

        foreach (var p in _procs)
        {
            try { await p.WaitForExitAsync(); } catch { }
        }

        try { await Task.WhenAll(_pumpTasks); } catch { }
    }

    private static async Task PumpAsync(StreamReader r, string prefix)
    {
        try
        {
            while (true)
            {
                var line = await r.ReadLineAsync();
                if (line == null) break;
                Console.WriteLine(prefix + line);
            }
        }
        catch { }
    }
}

public static class JsonHttp
{
    public static async Task<JsonElement> GetJsonAsync(string baseUrl, string path, CancellationToken ct = default)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var doc = await http.GetFromJsonAsync<JsonElement>(path, ct);
        return doc;
    }

    public static async Task<JsonElement> PostJsonAsync(string baseUrl, string path, object body, CancellationToken ct = default)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        using var resp = await http.PostAsJsonAsync(path, body, ct);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return doc;
    }
}

