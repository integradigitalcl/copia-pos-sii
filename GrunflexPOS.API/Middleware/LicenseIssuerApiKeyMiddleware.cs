namespace GrunflexPOS.API.Middleware;

/// <summary>Si <c>Licensing:IssuerApiKey</c> está definido, exige cabecera en rutas del emisor.</summary>
public sealed class LicenseIssuerApiKeyMiddleware
{
    private readonly RequestDelegate _next;

    public LicenseIssuerApiKeyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IConfiguration configuration)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/api/LicenseIssuer", StringComparison.OrdinalIgnoreCase))
        {
            var required = configuration["Licensing:IssuerApiKey"];
            if (!string.IsNullOrWhiteSpace(required))
            {
                var provided = context.Request.Headers["X-Grunflex-Issuer-Key"].FirstOrDefault();
                if (!string.Equals(provided, required, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsync("Se requiere X-Grunflex-Issuer-Key válido.", context.RequestAborted);
                    return;
                }
            }
        }

        await _next(context);
    }
}
