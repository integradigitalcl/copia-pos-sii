using System.Security.Cryptography;
using GrunflexPOS.API.DTOs;

namespace GrunflexPOS.API.Services;

/// <summary>Almacena archivos de respaldo por ActivationId (disco local del servidor API).</summary>
public sealed class TenantBackupStorage
{
    private readonly string _root;

    public TenantBackupStorage(IWebHostEnvironment env)
    {
        _root = Path.Combine(env.ContentRootPath, "Data", "tenant-backups");
        Directory.CreateDirectory(_root);
    }

    public async Task<(string RelativePath, long SizeBytes, string Sha256Hex)> SaveAsync(
        string activationId,
        string originalFileName,
        Stream body,
        CancellationToken cancellationToken)
    {
        var tenantDir = Path.Combine(_root, SanitizeSegment(activationId));
        Directory.CreateDirectory(tenantDir);

        var id = Guid.NewGuid();
        var ext = Path.GetExtension(originalFileName);
        if (string.IsNullOrEmpty(ext))
            ext = ".bin";

        var storageFileName = $"{id:N}{ext}";
        var fullPath = Path.Combine(tenantDir, storageFileName);

        await using (var fs = File.Create(fullPath))
        {
            await body.CopyToAsync(fs, cancellationToken);
        }

        var hashBytes = SHA256.HashData(await File.ReadAllBytesAsync(fullPath, cancellationToken));
        var fi = new FileInfo(fullPath);
        var relative = Path.Combine(SanitizeSegment(activationId), storageFileName);
        return (relative.Replace('\\', '/'), fi.Length, Convert.ToHexString(hashBytes));
    }

    public bool TryOpenRead(string relativePath, out Stream? stream, out string? error)
    {
        stream = null;
        error = null;
        var combined = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(Path.GetFullPath(_root), StringComparison.Ordinal))
        {
            error = "Ruta inválida.";
            return false;
        }

        if (!File.Exists(combined))
        {
            error = "Archivo no encontrado.";
            return false;
        }

        stream = File.OpenRead(combined);
        return true;
    }

    public bool TryDelete(string relativePath, out string? error)
    {
        error = null;
        var combined = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(Path.GetFullPath(_root), StringComparison.Ordinal))
        {
            error = "Ruta inválida.";
            return false;
        }

        if (!File.Exists(combined))
        {
            error = "Archivo no encontrado.";
            return false;
        }

        File.Delete(combined);
        return true;
    }

    /// <summary>Lista archivos de respaldo del tenant (últimos primero).</summary>
    public IReadOnlyList<TenantBackupFileInfo> ListForTenant(string activationId)
    {
        var seg = SanitizeSegment(activationId);
        var tenantDir = Path.Combine(_root, seg);
        if (!Directory.Exists(tenantDir))
            return Array.Empty<TenantBackupFileInfo>();

        var list = new List<TenantBackupFileInfo>();
        foreach (var fi in new DirectoryInfo(tenantDir).EnumerateFiles())
        {
            var rel = $"{seg}/{fi.Name}".Replace('\\', '/');
            list.Add(new TenantBackupFileInfo(rel, fi.Length, fi.LastWriteTimeUtc));
        }

        return list.OrderByDescending(x => x.StoredAtUtc).ToList();
    }

    /// <summary>Elimina solo si la ruta pertenece al activationId (anti path traversal).</summary>
    public bool TryDeleteForTenant(string activationId, string relativePath, out string? error)
    {
        error = null;
        var seg = SanitizeSegment(activationId);
        var normalized = relativePath.Replace('\\', '/').Trim().TrimStart('/');
        if (!normalized.StartsWith(seg + "/", StringComparison.OrdinalIgnoreCase))
        {
            error = "La ruta no pertenece a este ActivationId.";
            return false;
        }

        return TryDelete(normalized, out error);
    }

    private static string SanitizeSegment(string activationId)
    {
        var s = activationId.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return string.IsNullOrEmpty(s) ? "unknown" : s[..Math.Min(s.Length, 80)];
    }
}
