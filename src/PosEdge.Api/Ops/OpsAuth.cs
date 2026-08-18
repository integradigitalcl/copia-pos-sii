using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace PosEdge.Api.Ops;

public static class OpsAuth
{
    public static bool IsAuthorized(HttpRequest req, IConfiguration cfg)
    {
        var expected = cfg["Edge:OpsKey"];
        if (string.IsNullOrWhiteSpace(expected))
            return false;

        if (!req.Headers.TryGetValue("x-ops-key", out var got) || got.Count == 0)
            return false;

        return string.Equals(expected, got[0], StringComparison.Ordinal);
    }
}

