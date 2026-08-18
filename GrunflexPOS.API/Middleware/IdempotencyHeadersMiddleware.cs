using Grunflex.Idempotency;
using GrunflexPOS.API.Idempotency;

namespace GrunflexPOS.API.Middleware;

/// <summary>Extrae cabeceras de idempotencia y las deja en <see cref="IIdempotencyContextAccessor"/>.</summary>
public sealed class IdempotencyHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public IdempotencyHeadersMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IIdempotencyContextAccessor accessor)
    {
        var headers = context.Request.Headers;
        if (headers.TryGetValue(IdempotencyHeaderNames.RequestId, out var rid) &&
            !string.IsNullOrWhiteSpace(rid))
        {
            Guid? caja = null;
            if (headers.TryGetValue(IdempotencyHeaderNames.CajaId, out var cajaRaw) &&
                Guid.TryParse(cajaRaw.ToString(), out var cajaGuid))
                caja = cajaGuid;

            headers.TryGetValue(IdempotencyHeaderNames.Terminal, out var terminal);
            headers.TryGetValue(IdempotencyHeaderNames.PayloadHash, out var hash);

            accessor.Set(new IdempotencyRequestDescriptor
            {
                RequestId = rid.ToString().Trim(),
                TerminalId = terminal.ToString().Trim(),
                CajaId = caja,
                RequestHash = hash.ToString().Trim().ToLowerInvariant()
            });
        }

        await _next(context);
    }
}
