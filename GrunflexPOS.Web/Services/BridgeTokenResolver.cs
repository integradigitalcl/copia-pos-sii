using System.Security.Cryptography;
using System.Text;

namespace GrunflexPOS.Web.Services;

/// <summary>
/// Resolves the Hardware Bridge bearer token using the same DPAPI file as
/// GrunflexPOS.HardwareBridge (CurrentUser), with Development config fallback.
/// </summary>
public sealed class BridgeTokenResolver(IConfiguration configuration, IHostEnvironment environment, ILogger<BridgeTokenResolver> logger)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GrunflexPOS.HardwareBridge.v1");
    private readonly object _sync = new();
    private string? _cached;

    public string GetTokenPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GrunflexPOS",
            "HardwareBridge",
            "token.dpapi");

    public string Resolve()
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(_cached))
                return _cached;

            if (environment.IsDevelopment())
            {
                var development = FirstNonEmpty(
                    configuration["HardwareBridge:Token"],
                    configuration["HardwareBridge:DevelopmentToken"]);
                if (!string.IsNullOrWhiteSpace(development) &&
                    !IsPlaceholder(development) &&
                    development.Length >= 16)
                {
                    _cached = development;
                    return _cached;
                }
            }

            var fromFile = TryReadDpapiToken();
            if (!string.IsNullOrWhiteSpace(fromFile))
            {
                _cached = fromFile;
                return _cached;
            }

            var configured = configuration["HardwareBridge:Token"];
            if (!string.IsNullOrWhiteSpace(configured) &&
                !IsPlaceholder(configured) &&
                configured.Length >= 16)
            {
                _cached = configured;
                return _cached;
            }

            logger.LogWarning(
                "No se encontró token del Hardware Bridge. Inicie el bridge para generar {TokenPath}.",
                GetTokenPath());
            _cached = "missing-bridge-token";
            return _cached;
        }
    }

    /// <summary>Clears cache so the next Resolve() re-reads DPAPI after bridge startup.</summary>
    public void Invalidate()
    {
        lock (_sync)
            _cached = null;
    }

    private string? TryReadDpapiToken()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var path = GetTokenPath();
        if (!File.Exists(path))
            return null;

        try
        {
            var protectedBytes = Convert.FromBase64String(File.ReadAllText(path).Trim());
            var clearBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var token = Encoding.UTF8.GetString(clearBytes);
            return token.Length >= 32 ? token : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo leer el token DPAPI del bridge en {TokenPath}", path);
            return null;
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static bool IsPlaceholder(string value) =>
        value.Contains("replace-with", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("dev-local-token", StringComparison.OrdinalIgnoreCase);
}
