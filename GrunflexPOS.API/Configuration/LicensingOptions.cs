namespace GrunflexPOS.API.Configuration;

public sealed class LicensingOptions
{
    public const string SectionName = "Licensing";

    /// <summary>PEM RSA privada (solo servidor). Vacío hasta que exista api.secrets.json con bloque Licensing.</summary>
    public string PrivateKeyPem { get; set; } = string.Empty;

    public string PublicKeyPem { get; set; } = string.Empty;
}
