using Npgsql;

namespace PosEdge.IntegrationTests;

internal static class DbReset
{
    // Uses same defaults as appsettings.Development.json
    public const string Cs = "Host=127.0.0.1;Port=5432;Database=posedgedb;Username=posedge;Password=posedge;Include Error Detail=true";

    public static async Task ResetInventoryAsync(decimal onHand, decimal reserved = 0m, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(Cs);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          UPDATE inventory
                             SET on_hand = @on,
                                 reserved = @res,
                                 updated_at = now()
                           WHERE tenant_id = '11111111-1111-1111-1111-111111111111'
                             AND branch_id = '22222222-2222-2222-2222-222222222222'
                             AND product_id = '33333333-3333-3333-3333-333333333333';
                          """;
        cmd.Parameters.AddWithValue("@on", onHand);
        cmd.Parameters.AddWithValue("@res", reserved);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

