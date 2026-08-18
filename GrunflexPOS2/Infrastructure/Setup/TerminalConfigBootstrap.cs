using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using GrunflexPOS2.Data;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Infrastructure.Setup;

/// <summary>
/// En el primer arranque en un PC nuevo: si existe una plantilla generada desde el servidor
/// (Escritorio o carpeta del ejecutable), copia automáticamente a appsettings.local.json
/// (en %LocalAppData%\GrunflexPOS\config, escribible sin admin).
/// </summary>
public static class TerminalConfigBootstrap
{
    private const string TerminalMarker = "grunflex-terminal.json";

    public static void ApplyIfNeeded()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var userPath = AppConfig.UserLocalPath;
        var legacyPath = AppConfig.LegacyLocalPath;

        if (!IsLocalConfigIncomplete(userPath) || !IsLocalConfigIncomplete(legacyPath))
            return;

        var source = FindImportSource(baseDir);
        if (source == null)
            return;

        try
        {
            var json = File.ReadAllText(source);
            if (!TryValidateImportJson(json))
                return;

            var dir = Path.GetDirectoryName(userPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(userPath, json);

            // Evita volver a aplicar el mismo archivo junto al .exe en el siguiente arranque.
            if (IsUnderDirectory(source, baseDir) &&
                !string.Equals(source, userPath, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(source, legacyPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var applied = source + ".aplicado";
                    if (File.Exists(applied))
                        File.Delete(applied);
                    File.Move(source, applied);
                }
                catch
                {
                    // no bloquear inicio
                }
            }
        }
        catch
        {
            // dejar que AppConfig informe si sigue incompleto
        }
    }

    /// <summary>
    /// Copia la config de terminal a %ProgramData% para que coincida con el instalador y todos los usuarios.
    /// </summary>
    public static void PersistToMachineScopeIfNeeded()
    {
        try
        {
            if (!File.Exists(AppConfig.UserLocalPath))
                return;

            var userJson = File.ReadAllText(AppConfig.UserLocalPath);
            if (!TryValidateImportJson(userJson))
                return;

            var machinePath = AppConfig.MachineLocalPath;
            var dir = Path.GetDirectoryName(machinePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(machinePath))
            {
                try
                {
                    var existing = File.ReadAllText(machinePath);
                    if (string.Equals(existing.Trim(), userJson.Trim(), StringComparison.Ordinal))
                        return;
                }
                catch { }
            }

            File.WriteAllText(machinePath, userJson);
            PosDiagnostics.Log("TerminalConfigBootstrap: config copiada a ProgramData.");
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("PersistToMachineScopeIfNeeded", ex);
        }
    }

    private static bool IsUnderDirectory(string filePath, string directory)
    {
        var fullFile = Path.GetFullPath(filePath);
        var fullDir = Path.GetFullPath(directory);
        return fullFile.StartsWith(fullDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(fullFile, fullDir, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalConfigIncomplete(string localPath)
    {
        if (!File.Exists(localPath))
            return true;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(localPath));
            var root = doc.RootElement;
            if (!root.TryGetProperty("ConnectionStrings", out var csObj))
                return true;
            if (!csObj.TryGetProperty("Default", out var def))
                return true;
            var defStr = def.ValueKind == JsonValueKind.String ? def.GetString() : null;
            if (!string.IsNullOrWhiteSpace(defStr))
                return false;

            // Terminal API-only: hace falta API y CajaId (plantilla desde caja principal).
            if (IsApiOnlyClientConfig(root))
                return !HasValidCajaId(root);

            if (root.TryGetProperty("Api", out var api) &&
                api.TryGetProperty("BaseUrl", out var bu) &&
                bu.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(bu.GetString()))
                return false;

            return true;
        }
        catch
        {
            return true;
        }
    }

    private static bool TryValidateImportJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ConnectionStrings", out var csObj) || csObj.ValueKind != JsonValueKind.Object)
                return false;
            if (!csObj.TryGetProperty("Default", out var def) || def.ValueKind != JsonValueKind.String)
                return false;
            var cs = def.GetString();
            if (!string.IsNullOrWhiteSpace(cs))
                return true;

            if (!root.TryGetProperty("Api", out var api) ||
                !api.TryGetProperty("BaseUrl", out var bu) ||
                bu.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(bu.GetString()))
                return false;

            if (IsApiOnlyClientConfig(root))
                return HasValidCajaId(root);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsApiOnlyClientConfig(JsonElement root)
    {
        if (root.TryGetProperty("TerminalRole", out var role) &&
            role.ValueKind == JsonValueKind.String &&
            string.Equals(role.GetString(), "client", StringComparison.OrdinalIgnoreCase))
            return true;

        return root.TryGetProperty("Multicaja", out var mc) &&
               mc.TryGetProperty("UseApiOnlyClient", out var flag) &&
               ((flag.ValueKind == JsonValueKind.True) ||
                (flag.ValueKind == JsonValueKind.String &&
                 string.Equals(flag.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool HasValidCajaId(JsonElement root)
    {
        if (!root.TryGetProperty("CajaId", out var cid) || cid.ValueKind != JsonValueKind.String)
            return false;
        return Guid.TryParse(cid.GetString(), out var id) && id != Guid.Empty;
    }

    private static string? FindImportSource(string baseDir)
    {
        var terminal = Path.Combine(baseDir, TerminalMarker);
        if (File.Exists(terminal))
            return terminal;

        var inExe = Directory.GetFiles(baseDir, "grunflex-appsettings-*.json");
        var fromExe = inExe.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (fromExe != null)
            return fromExe;

        foreach (var folder in GetWellKnownFolders())
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                continue;
            try
            {
                var marker = Path.Combine(folder, TerminalMarker);
                if (File.Exists(marker))
                    return marker;

                var onDesk = Directory.GetFiles(folder, "grunflex-appsettings-*.json");
                var pick = onDesk.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (pick != null)
                    return pick;
            }
            catch
            {
                // ignorar
            }
        }

        return null;
    }

    private static string[] GetWellKnownFolders()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            return new[] { desktop, downloads };
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
