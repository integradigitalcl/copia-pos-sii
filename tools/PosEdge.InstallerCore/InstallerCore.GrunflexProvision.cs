using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PosEdge.InstallerCore;

public static partial class InstallerCore
{
    private const string GrunflexApiService = "GrunflexPOSAPI";

    public static string GrunflexProgramDataConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GrunflexPOS", "config");

    public static string GrunflexProgramDataDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GrunflexPOS", "data");

    public static string GrunflexUserConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GrunflexPOS", "config");

    public static void EnsureGrunflexServerProvisioned(string payloadRoot)
    {
        EnsureGrunflexDataDirectories();
        EnsureGrunflexApiSecrets();
        EnsureGrunflexApiProductionConfig(payloadRoot);
        WriteGrunflexAppSettings(role: "server", apiHost: "127.0.0.1", useLocalDb: true);

        var apiExe = Path.Combine(payloadRoot, "GrunflexApi", "GrunflexPOS.API.exe");
        if (File.Exists(apiExe))
        {
            InstallOrUpdateService(GrunflexApiService, apiExe, startAfterInstall: false);
            SetServiceEnvironmentVariable(GrunflexApiService, "GRUNFLEX_DATA_DIR", GrunflexProgramDataDataDir);
        }
    }

    public static void EnsureGrunflexApiSecrets()
    {
        EnsureGrunflexDataDirectories();
        var path = Path.Combine(GrunflexProgramDataDataDir, "api.secrets.json");
        if (File.Exists(path))
        {
            try
            {
                if (new FileInfo(path).Length > 10)
                    return;
            }
            catch { }
        }

        var signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var adminPassword = NewRandomHex(10) + "Aa1!";
        var payload = new
        {
            Jwt = new { SigningKey = signingKey },
            Security = new { AdminPassword = adminPassword },
            Licensing = new { IssuerApiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }

    public static void EnsureGrunflexApiProductionConfig(string payloadRoot)
    {
        var apiDir = Path.Combine(payloadRoot, "GrunflexApi");
        if (!Directory.Exists(apiDir))
            return;

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GrunflexPOS", "logs", "grunflex-api-.log").Replace("\\", "\\\\");
        var json = $$"""
{
  "Serilog": {
    "Using": [ "Serilog.Sinks.File" ],
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "Microsoft.AspNetCore": "Warning"
      }
    },
    "WriteTo": [
      {
        "Name": "File",
        "Args": {
          "path": "{{logPath}}",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 14
        }
      }
    ]
  }
}
""";
        File.WriteAllText(Path.Combine(apiDir, "appsettings.Production.json"), json, Encoding.UTF8);
    }

    public static void EnsureGrunflexClientProvisioned(string? posEdgeApiBase)
    {
        EnsureGrunflexDataDirectories();
        var host = ExtractHostFromApiUrl(posEdgeApiBase)
                   ?? ExtractHostFromApiUrl(ReadCachedServerUrl())
                   ?? ReadPersistedServerHost()
                   ?? ReadInstallServerHost();

        if (string.IsNullOrWhiteSpace(host) ||
            string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "No se configuró la IP de la caja principal. Reinstale como «Caja adicional» e indique la IP (ej. 192.168.1.7).");
        }

        WriteGrunflexAppSettings(role: "client", apiHost: host, useLocalDb: false);
    }

    public static void TryStartGrunflexApiProcess(string payloadRoot)
    {
        var apiExe = Path.Combine(payloadRoot, "GrunflexApi", "GrunflexPOS.API.exe");
        if (!File.Exists(apiExe))
            return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = apiExe,
                WorkingDirectory = Path.GetDirectoryName(apiExe)!,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            psi.Environment["DOTNET_ENVIRONMENT"] = "Production";
            psi.Environment["GRUNFLEX_DATA_DIR"] = GrunflexProgramDataDataDir;
            Process.Start(psi);
        }
        catch { }
    }

    public static void EnsureFirewallRuleTcp7279()
    {
        RunNetsh("advfirewall firewall delete rule name=\"Grunflex POS API (7279)\"");
        RunNetsh($"advfirewall firewall add rule name=\"Grunflex POS API (7279)\" dir=in action=allow protocol=TCP localport={InstallerCore.GrunflexApiPort} profile=any");
    }

    private static void EnsureGrunflexDataDirectories()
    {
        Directory.CreateDirectory(GrunflexProgramDataDataDir);
        Directory.CreateDirectory(GrunflexProgramDataConfigDir);
        Directory.CreateDirectory(GrunflexUserConfigDir);
        Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GrunflexPOS", "logs"));

        TryMigrateLegacySqlite();
        EnsureGrunflexMachineDataWritable();
    }

    private static void TryMigrateLegacySqlite()
    {
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GrunflexPOS", "data");
        if (!Directory.Exists(legacy))
            return;

        foreach (var file in Directory.GetFiles(legacy, "*.db"))
        {
            var dest = Path.Combine(GrunflexProgramDataDataDir, Path.GetFileName(file));
            if (!File.Exists(dest))
            {
                try { File.Copy(file, dest, overwrite: false); } catch { }
            }
        }
    }

    private static void WriteGrunflexAppSettings(string role, string apiHost, bool useLocalDb)
    {
        var dbPath = Path.Combine(GrunflexProgramDataDataDir, "grunflex.db");
        var shadowPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GrunflexPOS", "data", "terminal_shadow.db");
        if (!useLocalDb)
            Directory.CreateDirectory(Path.GetDirectoryName(shadowPath)!);

        var conn = useLocalDb
            ? $"Data Source={dbPath};Cache=Shared"
            : $"Data Source={shadowPath};Cache=Shared";

        var apiBase = $"http://{apiHost}:{InstallerCore.GrunflexApiPort}/";
        var doc = new Dictionary<string, object?>
        {
            ["ConnectionStrings"] = new Dictionary<string, string?> { ["Default"] = conn },
            ["Api"] = new Dictionary<string, string?>
            {
                ["BaseUrl"] = apiBase,
                ["PagoBaseUrl"] = $"http://{apiHost}:{InstallerCore.GrunflexApiPort}/api/pago"
            },
            ["CajaId"] = "",
            ["TerminalRole"] = role,
            ["Multicaja"] = new Dictionary<string, object?>
            {
                ["UseApiOnlyClient"] = !useLocalDb,
                ["RequireSharedSecret"] = false,
                ["SharedSecret"] = ""
            }
        };

        if (string.Equals(role, "client", StringComparison.OrdinalIgnoreCase))
        {
            doc["SmbShareUser"] = "GrunflexLan";
            doc["SmbSharePassword"] = "GrunflexLan2025SMB";
        }

        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(GrunflexProgramDataConfigDir, "appsettings.local.json"), json, Encoding.UTF8);
        File.WriteAllText(Path.Combine(GrunflexUserConfigDir, "appsettings.local.json"), json, Encoding.UTF8);
    }

    private static string? ReadCachedServerUrl()
    {
        try
        {
            var p = Path.Combine(ConfigDir, "cached-server.txt");
            if (!File.Exists(p)) return null;
            return File.ReadAllText(p, Encoding.UTF8).Trim();
        }
        catch { return null; }
    }

    public static string? ExtractHostFromApiUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;

        return string.IsNullOrWhiteSpace(uri.Host) ? null : uri.Host;
    }
}
