using System.IO;
using System.Text.Json;

namespace GrunflexPOS2.Data;

/// <summary>Lee rol terminal desde appsettings sin cargar AppConfig completo.</summary>
internal static class TerminalConfigProbe
{
    public static bool IsTerminalApiOnlyClient()
    {
        foreach (var path in new[] { AppConfig.MachineLocalPath, AppConfig.UserLocalPath, AppConfig.LegacyLocalPath })
        {
            if (!File.Exists(path))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var role = ReadString(root, "TerminalRole");
                if (string.Equals(role, "client", StringComparison.OrdinalIgnoreCase))
                {
                    if (root.TryGetProperty("Multicaja", out var mc) &&
                        mc.TryGetProperty("UseApiOnlyClient", out var flag) &&
                        flag.ValueKind == JsonValueKind.False)
                        return false;
                    return true;
                }

                if (root.TryGetProperty("Multicaja", out var m) &&
                    m.TryGetProperty("UseApiOnlyClient", out var u) &&
                    u.ValueKind == JsonValueKind.True)
                    return true;
            }
            catch
            {
                // noop
            }
        }

        return PosEdgeRoleProbe.IsPosEdgeTerminal();
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }
}
