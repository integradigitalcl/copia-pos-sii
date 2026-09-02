using System.Text.Json;

namespace GrunflexPOS.Web.Services.Licensing;

/// <summary>
/// Config crítica en %LocalAppData% (licencia). Compartida con el POS WPF en el mismo equipo.
/// </summary>
public static class LocalLicenseConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static string StorePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GrunflexPOS",
            "license",
            "local-config.json");

    public static bool IsLicenseKey(string key) =>
        key.StartsWith("licencia_", StringComparison.OrdinalIgnoreCase);

    public static string? TryGet(string key)
    {
        if (!IsLicenseKey(key))
            return null;

        try
        {
            var data = Load();
            return data.TryGetValue(key, out var value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Set(string key, string value)
    {
        if (!IsLicenseKey(key))
            return;

        var data = Load();
        data[key] = value ?? string.Empty;
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
