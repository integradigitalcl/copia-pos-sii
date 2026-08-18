using System;
using System.IO;

namespace GrunflexPOS2.Data;

/// <summary>
/// Rol PosEdge persistido por el instalador (%ProgramData%\PosEdge\config\role.txt).
/// Respaldo cuando appsettings.local.json quedó como caja principal por una instalación anterior.
/// </summary>
internal static class PosEdgeRoleProbe
{
    private static readonly string RolePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PosEdge",
        "config",
        "role.txt");

    public static bool IsPosEdgeTerminal()
    {
        try
        {
            if (!File.Exists(RolePath))
                return false;
            var role = File.ReadAllText(RolePath).Trim();
            return string.Equals(role, "terminal", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
