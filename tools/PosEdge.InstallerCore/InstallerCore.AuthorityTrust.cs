using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PosEdge.InstallerCore;

public sealed record TrustedAuthority(string ClusterId, string Fingerprint, string PublicKeyPem, long FirstSeenAtMs);

public static partial class InstallerCore
{
    public static string TrustDir => Path.Combine(ProgramDataRoot, "trust");

    public static TrustedAuthority? ReadTrustedAuthority()
    {
        try
        {
            var p = Path.Combine(TrustDir, "trusted-authority.json");
            if (!File.Exists(p)) return null;
            return JsonSerializer.Deserialize<TrustedAuthority>(File.ReadAllText(p, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    public static void PersistTrustedAuthority(TrustedAuthority auth)
    {
        Directory.CreateDirectory(TrustDir);
        var p = Path.Combine(TrustDir, "trusted-authority.json");
        File.WriteAllText(p, JsonSerializer.Serialize(auth, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }

    public static async Task<TrustedAuthority?> FetchAndValidateAuthorityAsync(string apiBase, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var resp = await http.GetAsync(apiBase.TrimEnd('/') + "/v1/cluster/identity", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var dto = JsonSerializer.Deserialize<ClusterIdentityDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto?.Ok != true) return null;

            if (string.IsNullOrWhiteSpace(dto.PublicKeyPem) || string.IsNullOrWhiteSpace(dto.ClusterId) ||
                string.IsNullOrWhiteSpace(dto.PrimaryServerId) || string.IsNullOrWhiteSpace(dto.Fingerprint) ||
                string.IsNullOrWhiteSpace(dto.SignedAuthorityToken) || dto.IssuedAtMs <= 0)
                return null;

            var fp = PosEdge.Shared.ClusterIdentityManager.ComputeFingerprint(dto.PublicKeyPem);
            if (!string.Equals(fp, dto.Fingerprint, StringComparison.OrdinalIgnoreCase))
                return null;

            var ok = PosEdge.Shared.ClusterIdentityManager.VerifyAuthorityToken(
                dto.PublicKeyPem, dto.ClusterId, dto.PrimaryServerId, dto.Fingerprint, dto.IssuedAtMs, dto.SignedAuthorityToken);
            if (!ok) return null;

            return new TrustedAuthority(dto.ClusterId, dto.Fingerprint, dto.PublicKeyPem, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch
        {
            return null;
        }
    }

    private sealed class ClusterIdentityDto
    {
        public bool Ok { get; set; }
        public string? ClusterId { get; set; }
        public string? PrimaryServerId { get; set; }
        public string? Fingerprint { get; set; }
        public string? PublicKeyPem { get; set; }
        public string? SignedAuthorityToken { get; set; }
        public long IssuedAtMs { get; set; }
    }
}

