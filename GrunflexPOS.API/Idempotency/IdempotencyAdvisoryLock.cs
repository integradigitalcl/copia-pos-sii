using System.Security.Cryptography;
using System.Text;
using GrunflexPOS.API.Data;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Idempotency;

/// <summary>pg_advisory_xact_lock (dos int32) derivados de RequestId + OperationType.</summary>
internal static class IdempotencyAdvisoryLock
{
    public static async Task AcquireAsync(ApiDbContext db, string requestId, string operationType, CancellationToken ct)
    {
        if (!IsPostgreSql(db))
            return;

        var (k1, k2) = DeriveKeys(requestId, operationType);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0}, {1});",
            new object[] { k1, k2 },
            ct);
    }

    private static bool IsPostgreSql(ApiDbContext db) =>
        db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

    private static (int k1, int k2) DeriveKeys(string requestId, string operationType)
    {
        var material = (requestId.Trim() + "|" + operationType.Trim()).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        var k1 = BitConverter.ToInt32(hash, 0);
        var k2 = BitConverter.ToInt32(hash, 4);
        return (k1, k2);
    }
}
