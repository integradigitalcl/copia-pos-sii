using PosEdge.InstallerCore;
using System.Linq;
using System.Text;

// PosEdge Bootstrapper — rol forzado por instalador o auto-detección.

var payloadRoot = ReadArg("--payload-root") ?? AppContext.BaseDirectory;
InstallerCore.EnsureConfigDir();
_ = InstallerCore.EnsureMachineId();

if (InstallerCore.IsInstallWipeDataRequested() || HasFlag("--wipe-data"))
{
    Console.WriteLine("Borrando datos anteriores...");
    InstallerCore.WipeApplicationDataForCleanInstall(payloadRoot);
    InstallerCore.ClearInstallWipeDataFlag();
}

var rolePath = Path.Combine(InstallerCore.ConfigDir, "role.txt");
var primaryLockPath = Path.Combine(InstallerCore.ConfigDir, "primary-server.lock");
var cachedServerPath = Path.Combine(InstallerCore.ConfigDir, "cached-server.txt");
var discoveryExe = Path.Combine(payloadRoot, "DiscoveryCli", "PosEdge.DiscoveryCli.exe");

var forcedRole = InstallerCore.ReadInstallForcedRole();
if (forcedRole != null)
{
    if (forcedRole == "server")
    {
        File.WriteAllText(rolePath, "server", Encoding.UTF8);
        if (!File.Exists(primaryLockPath))
            File.WriteAllText(primaryLockPath, "primary:" + Guid.NewGuid().ToString("D"), Encoding.UTF8);
        InstallerCore.ClearInstallRoleHintFiles();
        Console.WriteLine("Servidor listo.");
        Environment.Exit(10);
    }

    var manualHost = InstallerCore.ReadInstallServerHost();
    if (!string.IsNullOrWhiteSpace(manualHost))
        InstallerCore.PersistInstallServerHost(manualHost);

    var apiBase = await InstallerCore.ResolveGrunflexServerApiAsync(discoveryExe, CancellationToken.None);
    if (apiBase == null)
    {
        Console.WriteLine("No se encontró la caja principal en la red. Verifique IP del servidor e instalación en la PC principal.");
        Environment.Exit(20);
    }

    File.WriteAllText(rolePath, "terminal", Encoding.UTF8);
    File.WriteAllText(cachedServerPath, apiBase, Encoding.UTF8);
    InstallerCore.ClearInstallRoleHintFiles();
    Console.WriteLine("Terminal lista.");
    Environment.Exit(11);
}

// Sin rol forzado del instalador: si ya hay rol persistido, no auto-detectar de nuevo.
var existingRole = SafeRead(rolePath);
if (existingRole is "server" or "terminal")
{
    Console.WriteLine("Sistema listo.");
    Environment.Exit(string.Equals(existingRole, "server", StringComparison.OrdinalIgnoreCase) ? 10 : 11);
}

if (File.Exists(primaryLockPath))
{
    File.WriteAllText(rolePath, "server", Encoding.UTF8);
    Environment.Exit(10);
}

string? apiBaseAuto = null;
var cached = SafeRead(cachedServerPath);
if (!string.IsNullOrWhiteSpace(cached) && await InstallerCore.IsGrunflexApiHealthyAsync(cached.TrimEnd('/') + "/"))
    apiBaseAuto = cached.Trim().EndsWith("/") ? cached.Trim() : cached.Trim() + "/";

if (apiBaseAuto == null && File.Exists(discoveryExe))
{
    apiBaseAuto = await InstallerCore.ResolveGrunflexServerApiAsync(discoveryExe, CancellationToken.None);
}

if (apiBaseAuto != null)
{
    File.WriteAllText(rolePath, "terminal", Encoding.UTF8);
    File.WriteAllText(cachedServerPath, apiBaseAuto, Encoding.UTF8);
    Console.WriteLine("Terminal lista.");
    Environment.Exit(11);
}

var trusted = InstallerCore.ReadTrustedAuthority();
if (trusted != null)
{
    Console.WriteLine("Reconectando sistema...");
    Environment.Exit(20);
}

File.WriteAllText(rolePath, "server", Encoding.UTF8);
File.WriteAllText(primaryLockPath, "primary:" + Guid.NewGuid().ToString("D"), Encoding.UTF8);
Console.WriteLine("Servidor listo.");
Environment.Exit(10);

string? ReadArg(string key)
{
    var args = Environment.GetCommandLineArgs();
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

static string? SafeRead(string path)
{
    try
    {
        if (!File.Exists(path)) return null;
        return File.ReadAllText(path, Encoding.UTF8).Trim();
    }
    catch { return null; }
}

static bool HasFlag(string key) =>
    Environment.GetCommandLineArgs().Any(a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));
