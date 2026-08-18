using PosEdge.InstallerCore;
using System.Linq;
using System.Text;

// PosEdge SetupAgent
// - invoked by installer
// - applies role configuration without PowerShell
// - UX: only prints non-technical status strings
//
// Exit codes:
//   0 success
//   20 failure

try
{
    var payloadRoot = ReadArg("--payload-root") ?? AppContext.BaseDirectory;
    var repair = HasFlag("--repair");
    var role = (ReadText(Path.Combine(InstallerCore.ConfigDir, "role.txt")) ?? "").Trim().ToLowerInvariant();

    if (role != "server" && role != "terminal")
        throw new InvalidOperationException("Rol inválido.");

    if (role == "server")
    {
        Console.WriteLine(repair ? "Reparando servidor..." : "Preparando servidor...");
        ApplyServer(payloadRoot, repair);
        Console.WriteLine("Sistema listo.");
        return 0;
    }

    Console.WriteLine("Conectando terminal...");
    ApplyTerminal(payloadRoot);
    Console.WriteLine("Sistema listo.");
    return 0;
}
catch (Exception ex)
{
    InstallerCore.LogSetupFailure(ex);
    Console.WriteLine("No se pudo completar la instalación.");
    return 20;
}

static string? ReadArg(string key)
{
    var args = Environment.GetCommandLineArgs();
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

static string? ReadText(string path)
{
    try
    {
        if (!File.Exists(path)) return null;
        return File.ReadAllText(path, Encoding.UTF8);
    }
    catch { return null; }
}

static bool HasFlag(string key) =>
    Environment.GetCommandLineArgs().Any(a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));

static void ApplyServer(string payloadRoot, bool repair)
{
    // Provision server (PostgreSQL + DB bootstrap + appsettings) natively.
    _ = InstallerCore.EnsureServerProvisioned(payloadRoot);

    InstallerCore.EnsureFirewallRuleTcp5071();
    InstallerCore.EnsureFirewallRuleUdp33279();
    InstallerCore.EnsureFirewallRuleUdp5353();

    var apiExe = Path.Combine(payloadRoot, "Api", "PosEdge.Api.exe");
    var workersExe = Path.Combine(payloadRoot, "Workers", "PosEdge.Workers.exe");
    var guardianExe = Path.Combine(payloadRoot, "Guardian", "PosEdge.Guardian.exe");

    if (File.Exists(apiExe))
        InstallerCore.InstallOrUpdateService("PosEdgeApi", apiExe);

    if (File.Exists(workersExe))
        InstallerCore.InstallOrUpdateService("PosEdgeWorkers", workersExe);

    if (File.Exists(guardianExe))
        InstallerCore.InstallOrUpdateService("PosEdgeGuardian", guardianExe);

    InstallerCore.EnsureGrunflexServerProvisioned(payloadRoot);
    InstallerCore.EnsureFirewallRuleTcp7279();

    var grunflexApi = Path.Combine(payloadRoot, "GrunflexApi", "GrunflexPOS.API.exe");
    if (File.Exists(grunflexApi))
    {
        InstallerCore.InstallOrUpdateService("GrunflexPOSAPI", grunflexApi, startAfterInstall: true);
        InstallerCore.FinalizeGrunflexCommerceDataPermissions(waitForDbMs: 25_000);
    }

    RunMulticajaFirewallScript(payloadRoot, "Server");
}

static void ApplyTerminal(string payloadRoot)
{
    // Phase 1: re-discover server and write terminal appsettings.
    var discoveryExe = Path.Combine(payloadRoot, "DiscoveryCli", "PosEdge.DiscoveryCli.exe");
    if (!File.Exists(discoveryExe))
        throw new InvalidOperationException("Discovery missing.");

    var apiBase = InstallerCore.ResolveGrunflexServerApiAsync(discoveryExe, CancellationToken.None).GetAwaiter().GetResult();
    if (apiBase == null)
        throw new InvalidOperationException("No se encontró la caja principal (API puerto 7279).");

    // Validate and persist authority (TOFU: trust on first use).
    var authority = InstallerCore.FetchAndValidateAuthorityAsync(apiBase, CancellationToken.None).GetAwaiter().GetResult();
    if (authority == null)
        throw new InvalidOperationException("Servidor no autorizado.");

    var trusted = InstallerCore.ReadTrustedAuthority();
    if (trusted == null)
    {
        InstallerCore.PersistTrustedAuthority(authority);
    }
    else if (!string.Equals(trusted.Fingerprint, authority.Fingerprint, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Servidor distinto detectado.");
    }

    // Persist cache so bootstrapper and reconnect logic can use it.
    Directory.CreateDirectory(InstallerCore.ConfigDir);
    File.WriteAllText(Path.Combine(InstallerCore.ConfigDir, "cached-server.txt"), apiBase, Encoding.UTF8);
    File.WriteAllText(Path.Combine(InstallerCore.ConfigDir, "role.txt"), "terminal", Encoding.UTF8);

    InstallerCore.EnsureGrunflexClientProvisioned(apiBase);

    RunMulticajaFirewallScript(payloadRoot, "Client");
}

static void RunMulticajaFirewallScript(string payloadRoot, string role)
{
    var fw = Path.Combine(payloadRoot, "SetupExtras", "habilitar-firewall-multicaja.ps1");
    if (!File.Exists(fw))
        return;

    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{fw}\" -Role {role}",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = System.Diagnostics.Process.Start(psi);
        p?.WaitForExit(90_000);
    }
    catch { /* noop */ }
}
