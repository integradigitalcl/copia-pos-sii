using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GrunflexPOS2.Data;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Services.Multicaja.Sync;

/// <summary>
/// Estado de sincronización por dominio (JSON en carpeta de datos de la terminal).
/// </summary>
public sealed class SyncStateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string StateFilePath
    {
        get
        {
            LocalDatabasePaths.EnsureTerminalShadowParentExists();
            var dir = Path.GetDirectoryName(LocalDatabasePaths.TerminalClientShadowDatabasePath);
            return Path.Combine(dir ?? LocalDatabasePaths.LocalAppDataDirectory, "multicaja-sync-state.json");
        }
    }

    public SyncDomainState Get(string domain)
    {
        var all = Load();
        return all.TryGetValue(domain, out var s) ? s : new SyncDomainState { Domain = domain };
    }

    public bool IsStale(string domain, TimeSpan maxAge)
    {
        var s = Get(domain);
        if (s.LastSyncedUtc == DateTime.MinValue)
            return true;
        return DateTime.UtcNow - s.LastSyncedUtc > maxAge;
    }

    public void SaveSuccess(string domain, DateTime serverTimeUtc, int rowHint = 0, long cursor = 0)
    {
        var all = Load();
        var prev = Get(domain);
        all[domain] = new SyncDomainState
        {
            Domain = domain,
            LastSyncedUtc = serverTimeUtc == DateTime.MinValue ? DateTime.UtcNow : serverTimeUtc,
            LastCursor = cursor > 0 ? cursor : prev.LastCursor,
            LastError = null,
            RowHint = rowHint,
            RetryCount = 0
        };
        Save(all);
    }

    public void SaveFailure(string domain, string error)
    {
        var all = Load();
        var prev = Get(domain);
        all[domain] = new SyncDomainState
        {
            Domain = domain,
            LastSyncedUtc = prev.LastSyncedUtc,
            LastCursor = prev.LastCursor,
            LastError = error,
            RowHint = prev.RowHint,
            RetryCount = prev.RetryCount + 1
        };
        Save(all);
    }

    private static Dictionary<string, SyncDomainState> Load()
    {
        try
        {
            var path = StateFilePath;
            if (!File.Exists(path))
                return new Dictionary<string, SyncDomainState>(StringComparer.OrdinalIgnoreCase);
            var json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<SyncDomainState>>(json, JsonOpts);
            var dict = new Dictionary<string, SyncDomainState>(StringComparer.OrdinalIgnoreCase);
            if (list == null)
                return dict;
            foreach (var s in list)
            {
                if (!string.IsNullOrWhiteSpace(s.Domain))
                    dict[s.Domain] = s;
            }
            return dict;
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("SyncStateStore.Load", ex);
            return new Dictionary<string, SyncDomainState>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Save(Dictionary<string, SyncDomainState> all)
    {
        try
        {
            var path = StateFilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var list = new List<SyncDomainState>(all.Values);
            File.WriteAllText(path, JsonSerializer.Serialize(list, JsonOpts));
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("SyncStateStore.Save", ex);
        }
    }
}

public sealed class SyncDomainState
{
    public string Domain { get; set; } = "";
    public DateTime LastSyncedUtc { get; set; } = DateTime.MinValue;
    public long LastCursor { get; set; }
    public string? LastError { get; set; }
    public int RowHint { get; set; }
    public int RetryCount { get; set; }
}
