using Microsoft.AspNetCore.DataProtection;

namespace GrunflexPOS.API.Security;

public sealed class SensitiveDataEncryptionService
{
    private readonly IDataProtector _protector;

    public SensitiveDataEncryptionService(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("GrunflexPOS.API.SensitiveData.v1");
    }

    public string Encrypt(string plainText) => _protector.Protect(plainText);
    public string Decrypt(string cipherText) => _protector.Unprotect(cipherText);
}
