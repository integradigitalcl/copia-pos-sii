using System.Net.Http;
using System.Net.Http.Json;
using GrunflexPOS2.Services.Connectivity;

namespace GrunflexPOS2.Services.Multicaja.Sync;

/// <summary>Sync fina por IDs hacia SQLite shadow (sin PullTodo masivo).</summary>
public sealed class DeltaSyncService
{
    private readonly HttpClient _http;
    private readonly Func<ConnectivityState> _connectivity;

    public DeltaSyncService(HttpClient http, Func<ConnectivityState>? connectivity = null)
    {
        _http = http;
        _connectivity = connectivity ?? (() => ConnectivityState.Unknown);
    }

    public async Task<(bool Ok, string? Error, int Count)> PullProductsByIdsAsync(
        IReadOnlyList<int> productIds,
        CancellationToken ct = default)
    {
        if (!MulticajaRuntime.UseApiOnlyClient || productIds.Count == 0)
            return (true, null, 0);

        if (_connectivity() == ConnectivityState.Offline)
            return (false, "Sin conexión", 0);

        var ids = productIds.Where(i => i > 0).Distinct().ToList();
        if (ids.Count == 0)
            return (true, null, 0);

        try
        {
            var remoto = await MulticajaOperacionesClient.ListarProductosByIdsAsync(ids, ct)
                .ConfigureAwait(false);
            if (remoto == null)
                return (false, "No se pudo obtener productos por IDs.", 0);

            var applied = await MulticajaShadowCatalogSync.ApplyProductosAsync(remoto, ct)
                .ConfigureAwait(false);
            if (!applied.Ok)
                return (applied.Ok, applied.Error, 0);

            PosDiagnostics.Log($"multicaja.sync.byids products ok count={remoto.Count} ids={ids.Count}");
            return (true, null, remoto.Count);
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("multicaja.sync.byids products", ex);
            return (false, ex.Message, 0);
        }
    }

    public Task<(bool Ok, string? Error, int Count)> PullInventoryByIdsAsync(
        IReadOnlyList<int> productIds,
        CancellationToken ct = default) =>
        PullProductsByIdsAsync(productIds, ct);

    public async Task<(bool Ok, string? Error, int Count)> PullUsersByIdsAsync(
        IReadOnlyList<Guid> userIds,
        CancellationToken ct = default)
    {
        if (!MulticajaRuntime.UseApiOnlyClient || userIds.Count == 0)
            return (true, null, 0);

        if (_connectivity() == ConnectivityState.Offline)
            return (false, "Sin conexión", 0);

        // Sin endpoint por IDs de usuarios aún: pull completo de cajeros (ligero vs catálogo).
        PosDiagnostics.Log($"multicaja.sync.byids users fallback full count={userIds.Count}");
        var u = await MulticajaShadowCatalogSync.PullUsuariosAsync(ct).ConfigureAwait(false);
        return (u.Ok, u.Error, userIds.Count);
    }
}
