using System.Net.Http;
using System.Text;
using Grunflex.Idempotency;

namespace GrunflexPOS2.Services.Idempotency;

public static class IdempotencyHttpExtensions
{
    public static void ApplyIdempotencyHeaders<T>(this HttpRequestMessage request, T body, string requestId, Guid? cajaId)
    {
        var rid = (requestId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(rid))
            rid = Guid.NewGuid().ToString("N");

        request.Headers.Remove(IdempotencyHeaderNames.RequestId);
        request.Headers.Remove(IdempotencyHeaderNames.Terminal);
        request.Headers.Remove(IdempotencyHeaderNames.CajaId);
        request.Headers.Remove(IdempotencyHeaderNames.PayloadHash);

        request.Headers.TryAddWithoutValidation(IdempotencyHeaderNames.RequestId, rid);
        request.Headers.TryAddWithoutValidation(IdempotencyHeaderNames.Terminal, Environment.MachineName);
        if (cajaId.HasValue && cajaId.Value != Guid.Empty)
            request.Headers.TryAddWithoutValidation(IdempotencyHeaderNames.CajaId, cajaId.Value.ToString());
        request.Headers.TryAddWithoutValidation(
            IdempotencyHeaderNames.PayloadHash,
            IdempotencyPayloadHasher.HashPayload(body));
    }

    public static string ComputePayloadHash<T>(T body) => IdempotencyPayloadHasher.HashPayload(body);
}
