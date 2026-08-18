using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Grunflex.Licensing;

/// <summary>
/// Licencias <c>GFv2.&lt;payload_b64url&gt;.&lt;rsa_sig_b64url&gt;</c>.
/// La firma RSA-SHA256 (PKCS#1 v1.5) cubre los bytes ASCII del segmento payload_b64url.
/// </summary>
public static class GrunflexLicenseCodec
{
    public const string VersionPrefix = "GFv2";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string EncodeV2(GrunflexLicensePayload payload, RSA privateKey)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(privateKey);

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var p = Base64UrlEncode(jsonBytes);
        var signInput = Encoding.ASCII.GetBytes(p);
        var sigBytes = privateKey.SignData(signInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sig = Base64UrlEncode(sigBytes);
        return $"{VersionPrefix}.{p}.{sig}";
    }

    public static bool TryVerifyV2(string token, RSA publicKey, out GrunflexLicensePayload? payload, out string mensaje)
    {
        payload = null;
        mensaje = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            mensaje = "Licencia vacía.";
            return false;
        }

        var parts = token.Trim().Split('.', StringSplitOptions.None);
        if (parts.Length != 3 || !string.Equals(parts[0], VersionPrefix, StringComparison.Ordinal))
        {
            mensaje = "Formato de licencia GFv2 inválido.";
            return false;
        }

        var p = parts[1];
        byte[] sigBytes;
        try
        {
            sigBytes = Base64UrlDecodeBytes(parts[2]);
        }
        catch
        {
            mensaje = "Firma ilegible.";
            return false;
        }

        var signInput = Encoding.ASCII.GetBytes(p);
        if (!publicKey.VerifyData(signInput, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            mensaje = "Firma RSA inválida.";
            return false;
        }

        byte[] jsonBytes;
        try
        {
            jsonBytes = Base64UrlDecodeBytes(p);
        }
        catch
        {
            mensaje = "Contenido de licencia ilegible.";
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize<GrunflexLicensePayload>(jsonBytes, JsonOpts);
        }
        catch
        {
            mensaje = "No se pudo leer el contenido de la licencia.";
            return false;
        }

        if (payload == null)
        {
            mensaje = "Payload de licencia vacío.";
            return false;
        }

        mensaje = "Licencia válida.";
        return true;
    }

    public static RSA ImportPublicKeyFromPem(string pem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem.AsSpan());
        return rsa;
    }

    public static RSA ImportPrivateKeyFromPem(string pem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem.AsSpan());
        return rsa;
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecodeBytes(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }
}
