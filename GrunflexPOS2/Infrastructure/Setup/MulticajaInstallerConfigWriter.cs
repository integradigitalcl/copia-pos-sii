using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GrunflexPOS2.Data;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Infrastructure.Setup;

/// <summary>
/// Escribe la misma configuración de caja adicional API-only que el instalador Inno,
/// para que «Conectar a caja principal» y el asistente post-instalación sean coherentes.
/// </summary>
public static class MulticajaInstallerConfigWriter
{
    public const string InstallerHostMarkerFileName = ".multicaja-installer-host";

    public static string InstallerHostMarkerPath =>
        Path.Combine(
            Path.GetDirectoryName(AppConfig.MachineLocalPath) ?? string.Empty,
            InstallerHostMarkerFileName);

    /// <summary>Host del servidor (IP o nombre DNS/NetBIOS).</summary>
    public static void WriteApiOnlyClient(string serverHost)
    {
        serverHost = (serverHost ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(serverHost))
            throw new ArgumentException("Indique la IP o el nombre del servidor.", nameof(serverHost));

        LocalDatabasePaths.EnsureTerminalShadowParentExists();
        var cs = LocalDatabasePaths.TerminalClientShadowConnectionString;
        var apiBase = $"http://{serverHost}:7279/";
        var pagoBase = $"http://{serverHost}:7279/api/pago";

        var doc = new Dictionary<string, object?>
        {
            ["ConnectionStrings"] = new Dictionary<string, string?> { ["Default"] = cs },
            ["Api"] = new Dictionary<string, string?> { ["BaseUrl"] = apiBase, ["PagoBaseUrl"] = pagoBase },
            ["CajaId"] = "",
            ["TerminalRole"] = "client",
            ["SmbShareUser"] = MulticajaLanDefaults.ShareUser,
            ["SmbSharePassword"] = MulticajaLanDefaults.SharePassword,
            ["Multicaja"] = new Dictionary<string, object?>
            {
                ["UseApiOnlyClient"] = true,
                ["CatalogSyncIntervalSeconds"] = 5,
                ["SharedSecret"] = ""
            }
        };

        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });

        var dirM = Path.GetDirectoryName(AppConfig.MachineLocalPath);
        if (!string.IsNullOrEmpty(dirM))
            Directory.CreateDirectory(dirM);
        var dirU = Path.GetDirectoryName(AppConfig.UserLocalPath);
        if (!string.IsNullOrEmpty(dirU))
            Directory.CreateDirectory(dirU);

        File.WriteAllText(AppConfig.MachineLocalPath, json);
        File.WriteAllText(AppConfig.UserLocalPath, json);

        try
        {
            File.WriteAllText(InstallerHostMarkerPath, serverHost);
        }
        catch
        {
            // best-effort (para net use post-instalación)
        }

        PosDiagnostics.Log($"appsettings.local.json (API-only cliente) → servidor {serverHost} (ProgramData+LocalAppData).");
    }
}
