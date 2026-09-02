namespace GrunflexPOS.HardwareBridge.Security;

public static class LocalOriginPolicy
{
    public static bool IsAllowed(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return true;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            uri.UserInfo.Length != 0)
        {
            return false;
        }

        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);
    }
}
