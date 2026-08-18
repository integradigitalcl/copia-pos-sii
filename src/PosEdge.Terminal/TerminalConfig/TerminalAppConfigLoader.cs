using System.Text.Json;
using PosEdge.Terminal.Offline;

namespace PosEdge.Terminal.TerminalConfig;

public static class TerminalAppConfigLoader
{
    public static TerminalAppConfig Load(string[] args)
    {
        // Priority:
        // 1) CLI overrides (for ops/debug)
        // 2) appsettings (Production/Development)
        // 3) safe defaults (but never localhost in Production workflows)

        var cfg = ReadJsonConfig();

        var apiBase = FirstNonEmpty(
            args.Length > 0 ? args[0] : null,
            cfg.ApiBaseUrl,
            "http://127.0.0.1:5071");

        var tenantId = ParseGuidOrDefault(
            args.Length > 1 ? args[1] : null,
            cfg.TenantId,
            Guid.Parse("11111111-1111-1111-1111-111111111111"));

        var branchId = ParseGuidOrDefault(
            args.Length > 2 ? args[2] : null,
            cfg.BranchId,
            Guid.Parse("22222222-2222-2222-2222-222222222222"));

        var terminalId = ResolvePersistedGuid(
            key: "terminalId",
            argsOverride: args.Length > 3 ? args[3] : null,
            configValue: cfg.TerminalId);

        var cashSessionId = ResolvePersistedGuid(
            key: "cashSessionId",
            argsOverride: args.Length > 4 ? args[4] : null,
            configValue: cfg.CashSessionId,
            fallbackFactory: Guid.NewGuid);

        var offlineMode = ParseOfflineMode(
            args.Length > 5 ? args[5] : null,
            cfg.OfflineMode);

        var replicaPath = FirstNonEmpty(
            args.Length > 6 ? args[6] : null,
            cfg.ReplicaPath,
            DefaultReplicaPath(terminalId));

        PersistIfMissing("terminalId", terminalId);
        PersistIfMissing("cashSessionId", cashSessionId);

        return new TerminalAppConfig(
            apiBase.TrimEnd('/'),
            tenantId,
            branchId,
            terminalId,
            cashSessionId,
            offlineMode,
            replicaPath);
    }

    private static string DefaultReplicaPath(Guid terminalId)
    {
        // One replica DB per terminal instance.
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PosEdge",
            $"terminal-{terminalId:D}.sqlite");
    }

    private static OfflineMode ParseOfflineMode(string? arg, OfflineMode fromCfg)
    {
        if (!string.IsNullOrWhiteSpace(arg))
        {
            if (Enum.TryParse<OfflineMode>(arg, ignoreCase: true, out var m))
                return m;
            if (string.Equals(arg, "leases_only", StringComparison.OrdinalIgnoreCase))
                return OfflineMode.LeasesOnly;
            if (string.Equals(arg, "strict", StringComparison.OrdinalIgnoreCase))
                return OfflineMode.Strict;
            if (string.Equals(arg, "permissive", StringComparison.OrdinalIgnoreCase))
                return OfflineMode.Permissive;
        }
        return fromCfg;
    }

    private static Guid ResolvePersistedGuid(string key, string? argsOverride, Guid configValue, Func<Guid>? fallbackFactory = null)
    {
        if (!string.IsNullOrWhiteSpace(argsOverride) && Guid.TryParse(argsOverride, out var argG))
            return argG;
        if (configValue != Guid.Empty)
            return configValue;
        var persisted = TryReadPersistedGuid(key);
        if (persisted != null)
            return persisted.Value;
        return (fallbackFactory ?? Guid.NewGuid).Invoke();
    }

    private static void PersistIfMissing(string key, Guid value)
    {
        if (TryReadPersistedGuid(key) != null)
            return;
        WritePersistedText(key, value.ToString("D"));
    }

    private static Guid? TryReadPersistedGuid(string key)
    {
        try
        {
            var p = PersistPath(key);
            if (!File.Exists(p))
                return null;
            var s = File.ReadAllText(p).Trim();
            return Guid.TryParse(s, out var g) ? g : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WritePersistedText(string key, string value)
    {
        try
        {
            var p = PersistPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, value);
        }
        catch
        {
            // Best-effort persistence; terminal still runs.
        }
    }

    private static string PersistPath(string key)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PosEdge",
            "terminal-config",
            $"{key}.txt");
    }

    private static Guid ParseGuidOrDefault(string? arg, Guid cfgValue, Guid fallback)
    {
        if (!string.IsNullOrWhiteSpace(arg) && Guid.TryParse(arg, out var g))
            return g;
        if (cfgValue != Guid.Empty)
            return cfgValue;
        return fallback;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v!;
        }
        return "";
    }

    private static (string ApiBaseUrl, Guid TenantId, Guid BranchId, Guid TerminalId, Guid CashSessionId, OfflineMode OfflineMode, string ReplicaPath) ReadJsonConfig()
    {
        // Terminal supports simple config in appsettings.*.json:
        // { "Api": { "BaseUrl": "http://server:5071" }, "Terminal": { ... } }
        // We do not use full Microsoft.Extensions.Configuration to keep the terminal tiny.
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var paths = new[]
            {
                Path.Combine(baseDir, "appsettings.Production.json"),
                Path.Combine(baseDir, "appsettings.json"),
                Path.Combine(baseDir, "appsettings.Development.json")
            };

            foreach (var p in paths)
            {
                if (!File.Exists(p))
                    continue;
                var json = File.ReadAllText(p);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var apiBase = root.TryGetProperty("Api", out var apiEl) && apiEl.TryGetProperty("BaseUrl", out var baseEl) && baseEl.ValueKind == JsonValueKind.String
                    ? (baseEl.GetString() ?? "")
                    : "";

                Guid ReadGuid(string section, string key)
                {
                    if (!root.TryGetProperty(section, out var sec) || !sec.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
                        return Guid.Empty;
                    return Guid.TryParse(el.GetString(), out var g) ? g : Guid.Empty;
                }

                string ReadString(string section, string key)
                {
                    if (!root.TryGetProperty(section, out var sec) || !sec.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
                        return "";
                    return el.GetString() ?? "";
                }

                OfflineMode ReadMode()
                {
                    var s = ReadString("Terminal", "OfflineMode");
                    if (string.IsNullOrWhiteSpace(s))
                        return OfflineMode.LeasesOnly;
                    if (Enum.TryParse<OfflineMode>(s, ignoreCase: true, out var m))
                        return m;
                    if (string.Equals(s, "leases_only", StringComparison.OrdinalIgnoreCase))
                        return OfflineMode.LeasesOnly;
                    if (string.Equals(s, "strict", StringComparison.OrdinalIgnoreCase))
                        return OfflineMode.Strict;
                    if (string.Equals(s, "permissive", StringComparison.OrdinalIgnoreCase))
                        return OfflineMode.Permissive;
                    return OfflineMode.LeasesOnly;
                }

                var tenantId = ReadGuid("Terminal", "TenantId");
                var branchId = ReadGuid("Terminal", "BranchId");
                var terminalId = ReadGuid("Terminal", "TerminalId");
                var cashSessionId = ReadGuid("Terminal", "CashSessionId");
                var replicaPath = ReadString("Terminal", "ReplicaPath");
                var mode = ReadMode();

                return (apiBase, tenantId, branchId, terminalId, cashSessionId, mode, replicaPath);
            }
        }
        catch
        {
            // ignore
        }

        return ("", Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, OfflineMode.LeasesOnly, "");
    }
}

