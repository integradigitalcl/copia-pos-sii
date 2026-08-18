using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GrunflexPOS2.Data;

namespace GrunflexPOS2.Services.Multicaja.Terminal;

/// <summary>
/// Identidad persistente de terminal (independiente de hostname/IP/MAC).
/// </summary>
public sealed class TerminalIdentityStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string IdentityFilePath =>
        Path.Combine(LocalDatabasePaths.LocalAppDataDirectory, "terminal", "identity.json");

    public TerminalIdentitySnapshot LoadOrCreate()
    {
        try
        {
            if (File.Exists(IdentityFilePath))
            {
                var json = File.ReadAllText(IdentityFilePath);
                var snap = JsonSerializer.Deserialize<TerminalIdentitySnapshot>(json, JsonOpts);
                if (snap != null && snap.InstallationId != Guid.Empty)
                {
                    if (string.IsNullOrWhiteSpace(snap.TerminalToken))
                        snap.TerminalToken = GenerateToken();
                    return snap;
                }
            }
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("TerminalIdentityStore.Load", ex);
        }

        var created = new TerminalIdentitySnapshot
        {
            InstallationId = Guid.NewGuid(),
            TerminalToken = GenerateToken(),
            CreatedAtUtc = DateTime.UtcNow
        };
        Save(created);
        return created;
    }

    public void Save(TerminalIdentitySnapshot snap)
    {
        var dir = Path.GetDirectoryName(IdentityFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = IdentityFilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snap, JsonOpts));
        if (File.Exists(IdentityFilePath)) File.Delete(IdentityFilePath);
        File.Move(tmp, IdentityFilePath);
    }

    public static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes);
    }
}

public sealed class TerminalIdentitySnapshot
{
    public Guid InstallationId { get; set; }
    public Guid? TerminalId { get; set; }
    public string TerminalToken { get; set; } = "";
    public Guid? CajaId { get; set; }
    public Guid? BranchId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? RegisteredAtUtc { get; set; }
    public string? DisplayName { get; set; }
}
