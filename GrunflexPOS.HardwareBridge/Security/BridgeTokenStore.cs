using System.Security.Cryptography;
using System.Text;
using GrunflexPOS.HardwareBridge.Configuration;
using Microsoft.Extensions.Options;

namespace GrunflexPOS.HardwareBridge.Security;

public sealed class BridgeTokenStore
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("GrunflexPOS.HardwareBridge.v1");

    private readonly HardwareBridgeOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<BridgeTokenStore> _logger;
    private readonly object _sync = new();
    private string? _token;

    public BridgeTokenStore(
        IOptions<HardwareBridgeOptions> options,
        IHostEnvironment environment,
        ILogger<BridgeTokenStore> logger)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public string GetToken()
    {
        lock (_sync)
        {
            if (_token is not null)
                return _token;

            if (_environment.IsDevelopment() &&
                !string.IsNullOrWhiteSpace(_options.DevelopmentToken))
            {
                if (_options.DevelopmentToken.Length < 16)
                    throw new InvalidOperationException(
                        "HardwareBridge:DevelopmentToken must contain at least 16 characters.");

                _token = _options.DevelopmentToken;
                _logger.LogWarning("Using the configured development bridge token.");
                return _token;
            }

            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "The bridge requires Windows DPAPI for its installation token.");

            var path = GetTokenPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (File.Exists(path))
            {
                var protectedBytes = Convert.FromBase64String(File.ReadAllText(path).Trim());
                var clearBytes = ProtectedData.Unprotect(
                    protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                _token = Encoding.UTF8.GetString(clearBytes);
                if (_token.Length < 32)
                    throw new InvalidDataException("The bridge token file is invalid.");
                return _token;
            }

            _token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(_token), Entropy, DataProtectionScope.CurrentUser);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporaryPath, Convert.ToBase64String(encrypted), Encoding.UTF8);
            File.Move(temporaryPath, path);
            _logger.LogInformation("Created the per-install DPAPI bridge token at {TokenPath}.", path);
            return _token;
        }
    }

    public string GetTokenPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GrunflexPOS",
            "HardwareBridge",
            "token.dpapi");
}
