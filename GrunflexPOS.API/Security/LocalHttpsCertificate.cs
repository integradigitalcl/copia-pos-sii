using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GrunflexPOS.API.Configuration;

namespace GrunflexPOS.API.Security;

/// <summary>
/// Genera y persiste un certificado self-signed para uso del endpoint HTTPS local opcional
/// (Fase 5.2). Se crea en <c>%ProgramData%\GrunflexPOS\secrets\local-https.pfx</c> con clave
/// aleatoria local. NO es un cert público — es para cifrar tráfico LAN entre cajas y la API
/// del servidor, evitando passwords y JWT en claro.
///
/// Para evitar warnings del cliente, el POS confía explícitamente en este cert
/// (TrustedThumbprints en appsettings o pinning en el cliente HTTP).
/// </summary>
public static class LocalHttpsCertificate
{
    public static string CertPath => Path.Combine(ApiPaths.SecretsDirectory, "local-https.pfx");
    public static string PasswordPath => Path.Combine(ApiPaths.SecretsDirectory, "local-https.pwd");

    public static X509Certificate2 EnsureCertificate(int validityYears = 5)
    {
        Directory.CreateDirectory(ApiPaths.SecretsDirectory);
        if (File.Exists(CertPath) && File.Exists(PasswordPath))
        {
            try
            {
                var pwd = File.ReadAllText(PasswordPath).Trim();
                var existing = new X509Certificate2(CertPath, pwd, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
                if (existing.NotAfter > DateTime.UtcNow.AddDays(30))
                    return existing;
            }
            catch { /* recreamos si está corrupto/expirado */ }
        }

        var pwdNew = GenerateRandomPassword();
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=GrunflexPOS Local, OU=Multicaja, O=Grunflex", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(Environment.MachineName);
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
        req.CertificateExtensions.Add(sanBuilder.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") /* server auth */ }, false));

        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(validityYears));

        var pfxBytes = cert.Export(X509ContentType.Pfx, pwdNew);
        File.WriteAllBytes(CertPath, pfxBytes);
        File.WriteAllText(PasswordPath, pwdNew);

        return new X509Certificate2(CertPath, pwdNew, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
    }

    private static string GenerateRandomPassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes);
    }
}
