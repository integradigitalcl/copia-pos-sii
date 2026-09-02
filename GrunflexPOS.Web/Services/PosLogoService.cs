namespace GrunflexPOS.Web.Services;

public sealed class PosLogoService(LocalPosStore store, IWebHostEnvironment environment)
{
    public const string DefaultLogoPath = "assets/logo_custom.png";
    public const string ApiLogoPath = "api/pos-logo";

    public async Task<string> GetDisplayUrlAsync(CancellationToken cancellationToken = default)
    {
        // Never embed base64 into Blazor circuit state (large payloads break/omit the image).
        // Serve through a stable HTTP endpoint instead.
        var stamp = await GetCacheStampAsync(cancellationToken);
        return $"{ApiLogoPath}?v={stamp}";
    }

    public async Task<byte[]?> GetLogoBytesAsync(CancellationToken cancellationToken = default)
    {
        var logoData = await store.GetSettingAsync("logo_data", string.Empty, cancellationToken);
        var fromSetting = TryDecodeDataUri(logoData);
        if (fromSetting is { Length: > 0 })
            return fromSetting;

        foreach (var candidate in ResolveCandidatePaths())
        {
            if (!File.Exists(candidate))
                continue;

            var bytes = await File.ReadAllBytesAsync(candidate, cancellationToken);
            if (bytes.Length > 0)
                return bytes;
        }

        return null;
    }

    private async Task<string> GetCacheStampAsync(CancellationToken cancellationToken)
    {
        var logoData = await store.GetSettingAsync("logo_data", string.Empty, cancellationToken);
        if (!string.IsNullOrWhiteSpace(logoData))
            return Math.Abs(logoData.GetHashCode()).ToString();

        foreach (var candidate in ResolveCandidatePaths())
        {
            if (!File.Exists(candidate))
                continue;
            return File.GetLastWriteTimeUtc(candidate).Ticks.ToString();
        }

        return DateTime.UtcNow.Ticks.ToString();
    }

    private IEnumerable<string> ResolveCandidatePaths()
    {
        var webRoot = environment.WebRootPath;
        if (!string.IsNullOrWhiteSpace(webRoot))
        {
            yield return Path.Combine(webRoot, "assets", "logo_custom.png");
            yield return Path.Combine(webRoot, "assets", "logo.png");
        }

        var contentRoot = environment.ContentRootPath;
        if (!string.IsNullOrWhiteSpace(contentRoot))
        {
            yield return Path.Combine(contentRoot, "wwwroot", "assets", "logo_custom.png");
            yield return Path.Combine(contentRoot, "wwwroot", "assets", "logo.png");
            yield return Path.Combine(contentRoot, "assets", "logo_custom.png");
            yield return Path.Combine(contentRoot, "assets", "logo.png");
        }

        yield return Path.Combine(AppContext.BaseDirectory, "wwwroot", "assets", "logo_custom.png");
        yield return Path.Combine(AppContext.BaseDirectory, "wwwroot", "assets", "logo.png");
        yield return Path.Combine(AppContext.BaseDirectory, "assets", "logo_custom.png");
        yield return Path.Combine(AppContext.BaseDirectory, "assets", "logo.png");
    }

    private static byte[]? TryDecodeDataUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;

        var comma = value.IndexOf(',');
        if (comma <= 0)
            return null;

        try
        {
            var bytes = Convert.FromBase64String(value[(comma + 1)..]);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
