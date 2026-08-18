using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace GrunflexPOS.API.Security;

/// <summary>
/// LAN: si <c>Multicaja:SharedSecret</c> está definido, exige cabecera <c>X-Grunflex-Multicaja-Key</c>.
/// Si <c>Multicaja:RequireSharedSecret</c> es true, el secreto no puede estar vacío y la cabecera es obligatoria.
/// </summary>
public sealed class MulticajaSharedSecretFilter : IAsyncActionFilter
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<MulticajaSharedSecretFilter> _log;

    public MulticajaSharedSecretFilter(IConfiguration cfg, ILogger<MulticajaSharedSecretFilter> log)
    {
        _cfg = cfg;
        _log = log;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var require = _cfg.GetValue("Multicaja:RequireSharedSecret", false);
        var expected = (_cfg["Multicaja:SharedSecret"] ?? string.Empty).Trim();

        if (require && string.IsNullOrEmpty(expected))
        {
            _log.LogError("multicaja.auth RequireSharedSecret=true pero Multicaja:SharedSecret está vacío.");
            context.Result = new ObjectResult(new { error = "Servidor mal configurado (Multicaja sin secreto)." })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
            return;
        }

        if (!require && string.IsNullOrEmpty(expected))
        {
            await next();
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue("X-Grunflex-Multicaja-Key", out var sent) ||
            !string.Equals(sent.ToString(), expected, StringComparison.Ordinal))
        {
            _log.LogWarning("multicaja.auth denied from {Ip} (bad or missing multicaja key)",
                context.HttpContext.Connection.RemoteIpAddress);
            context.Result = new UnauthorizedObjectResult(new { error = "Multicaja key inválida o ausente." });
            return;
        }

        await next();
    }
}
