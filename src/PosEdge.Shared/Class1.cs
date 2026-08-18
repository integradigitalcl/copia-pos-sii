using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PosEdge.Shared;

public sealed record ClusterIdentity(
    string ClusterId,
    string PrimaryServerId,
    string Fingerprint,
    string PublicKeyPem,
    string SignedAuthorityToken,
    long IssuedAtMs);

public static class ClusterIdentityManager
{
    public static string ProgramDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PosEdge");

    public static string ClusterDir => Path.Combine(ProgramDataRoot, "cluster");

    private sealed record StoredIdentity(string ClusterId, string PrimaryServerId, string PublicKeyPem, string PrivateKeyPem);

    public static ClusterIdentity GetOrCreateIdentity()
    {
        Directory.CreateDirectory(ClusterDir);
        var path = Path.Combine(ClusterDir, "cluster-identity.json");

        var stored = ReadStored(path) ?? CreateNew(path);
        var fp = ComputeFingerprint(stored.PublicKeyPem);
        var issuedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var token = SignAuthorityToken(stored.PrivateKeyPem, stored.ClusterId, stored.PrimaryServerId, fp, issuedAtMs);

        return new ClusterIdentity(stored.ClusterId, stored.PrimaryServerId, fp, stored.PublicKeyPem, token, issuedAtMs);
    }

    public static bool VerifyAuthorityToken(string publicKeyPem, string clusterId, string primaryServerId, string fingerprint, long issuedAtMs, string token)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            var payload = BuildPayload(clusterId, primaryServerId, fingerprint, issuedAtMs);
            var sig = Convert.FromBase64String(token);
            return rsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }

    public static string ComputeFingerprint(string publicKeyPem)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(publicKeyPem.Trim());
        var h = sha.ComputeHash(bytes);
        return Convert.ToHexString(h).ToLowerInvariant();
    }

    private static StoredIdentity? ReadStored(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<StoredIdentity>(File.ReadAllText(path, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    private static StoredIdentity CreateNew(string path)
    {
        using var rsa = RSA.Create(3072);
        var pub = ExportPublicPem(rsa);
        var priv = ExportPrivatePem(rsa);
        var stored = new StoredIdentity(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), pub, priv);
        File.WriteAllText(path, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        return stored;
    }

    private static byte[] BuildPayload(string clusterId, string primaryServerId, string fingerprint, long issuedAtMs)
        => Encoding.UTF8.GetBytes($"{clusterId}|{primaryServerId}|{fingerprint}|{issuedAtMs}");

    private static string SignAuthorityToken(string privateKeyPem, string clusterId, string primaryServerId, string fingerprint, long issuedAtMs)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var payload = BuildPayload(clusterId, primaryServerId, fingerprint, issuedAtMs);
        var sig = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(sig);
    }

    private static string ExportPublicPem(RSA rsa)
    {
        var b = rsa.ExportSubjectPublicKeyInfo();
        return PemEncode("PUBLIC KEY", b);
    }

    private static string ExportPrivatePem(RSA rsa)
    {
        var b = rsa.ExportPkcs8PrivateKey();
        return PemEncode("PRIVATE KEY", b);
    }

    private static string PemEncode(string label, byte[] data)
    {
        var base64 = Convert.ToBase64String(data);
        var sb = new StringBuilder();
        sb.AppendLine($"-----BEGIN {label}-----");
        for (var i = 0; i < base64.Length; i += 64)
            sb.AppendLine(base64.Substring(i, Math.Min(64, base64.Length - i)));
        sb.AppendLine($"-----END {label}-----");
        return sb.ToString();
    }
}
