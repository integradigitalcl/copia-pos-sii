using Grunflex.Licensing;
using GrunflexPOS.API.Configuration;
using Microsoft.Extensions.Options;

namespace GrunflexPOS.API.Services;

/// <summary>Firma tokens GFv2 para endpoints públicos de licencia.</summary>
public sealed class LicensingIssueService
{
    private readonly LicensingOptions _options;

    public LicensingIssueService(IOptions<LicensingOptions> options)
    {
        _options = options.Value;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.PrivateKeyPem);

    public string? PublicKeyPem =>
        string.IsNullOrWhiteSpace(_options.PublicKeyPem)
            ? null
            : _options.PublicKeyPem.Trim();

    public string SignPayload(GrunflexLicensePayload payload)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("No hay clave privada de licencias configurada (Licensing:PrivateKeyPem).");

        using var rsa = GrunflexLicenseCodec.ImportPrivateKeyFromPem(_options.PrivateKeyPem);
        return GrunflexLicenseCodec.EncodeV2(payload, rsa);
    }
}
