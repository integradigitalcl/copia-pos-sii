using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GrunflexPOS2.Licensing;

/// <summary>
/// Config crítica en %LocalAppData% (licencia, instalación). No depende de SQLite
/// ni de UNC; evita fallos al activar licencia si la BD local no está migrada o está bloqueada.
/// </summary>
internal static class LocalPosConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private static string StorePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GrunflexPOS",
            "license",
            "local-config.json");

    public static bool IsLocalFallbackKey(string clave) =>
        clave.StartsWith("licencia_", StringComparison.OrdinalIgnoreCase)
        || string.Equals(clave, "instalacion_completada", StringComparison.OrdinalIgnoreCase);

    public static string? TryGet(string clave)
    {
        if (!IsLocalFallbackKey(clave))
            return null;

        try
        {
            var data = Load();
            return data.TryGetValue(clave, out var v) ? v : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Set(string clave, string valor)
    {
        if (!IsLocalFallbackKey(clave))
            return;

        var data = Load();
        data[clave] = valor ?? string.Empty;
        Save(data);
    }

    private static Dictionary<string, string> Load()
    {
        if (!File.Exists(StorePath))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var json = File.ReadAllText(StorePath);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
               ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void Save(Dictionary<string, string> data)
    {
        var dir = Path.GetDirectoryName(StorePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(StorePath, JsonSerializer.Serialize(data, JsonOptions));
    }
}
