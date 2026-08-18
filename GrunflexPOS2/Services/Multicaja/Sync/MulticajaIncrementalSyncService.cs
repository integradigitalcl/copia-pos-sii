using System.Net.Http;
using System.Net.Http.Json;
using GrunflexPOS2.Services.Connectivity;

namespace GrunflexPOS2.Services.Multicaja.Sync;

/// <summary>Delta pull por cursor; aplica cambios vía sync por IDs (no PullProductos completo).</summary>
public sealed class MulticajaIncrementalSyncService
{
    private readonly HttpClient _http;
    private readonly SyncStateStore _state = new();
    private readonly Func<ConnectivityState> _connectivity;
    private readonly MulticajaSyncCoordinator? _coordinator;

    public MulticajaIncrementalSyncService(
        HttpClient http,
        Func<ConnectivityState>? connectivity = null,
        MulticajaSyncCoordinator? Coordinator = null)
    {
        _http = http;
        _connectivity = connectivity ?? (() => ConnectivityState.Unknown);
        _coordinator = Coordinator;
    }

    public async Task<(bool Ok, string? Error, int ChangeCount)> PullChangesAsync(CancellationToken ct = default)
    {
        if (!MulticajaRuntime.UseApiOnlyClient)
            return (false, "No aplica", 0);

        if (_connectivity() == ConnectivityState.Offline)
            return (false, "Sin conexión", 0);

        var catalogState = _state.Get("catalog");
        var cursor = catalogState.LastCursor;

        try
        {
            var url = $"api/multicaja/sync/changes?cursor={cursor}&limit=500&domains=products,inventory,users,cajas,config";
            var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode}", 0);

            var body = await resp.Content.ReadFromJsonAsync<SyncChangesDto>(cancellationToken: ct).ConfigureAwait(false);
            if (body == null)
                return (false, "Respuesta vacía", 0);

            if (body.ResetRequired)
            {
                PosDiagnostics.Log("multicaja.sync reset_required → full pull");
                var full = await MulticajaShadowCatalogSync.PullTodoAsync(ct).ConfigureAwait(false);
                if (full.Ok)
                    _state.SaveSuccess("catalog", body.ServerTimeUtc, cursor: body.NextCursor);
                return (full.Ok, full.Error, 0);
            }

            var changes = body.Changes ?? new List<SyncChangeDto>();
            if (changes.Count == 0 && cursor == 0)
            {
                var bootstrap = await MulticajaShadowCatalogSync.PullTodoAsync(ct).ConfigureAwait(false);
                if (bootstrap.Ok)
                    _state.SaveSuccess("catalog", body.ServerTimeUtc, cursor: body.NextCursor);
                return (bootstrap.Ok, bootstrap.Error, 0);
            }

            var productIds = changes
                .Where(c => c.Domain is "products" or "inventory")
                .Select(c => int.TryParse(c.EntityId, out var id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            var needUsers = changes.Any(c => c.Domain == "users");

            if (productIds.Count > 0 && _coordinator != null)
            {
                _coordinator.EnqueueProductIds(productIds, "incremental-changes");
                await _coordinator.FlushPendingBatchesAsync(ct).ConfigureAwait(false);
            }
            else if (productIds.Count > 0)
            {
                await MulticajaShadowCatalogSync.PullProductsByIdsAsync(productIds, ct).ConfigureAwait(false);
            }

            if (needUsers)
            {
                var u = await MulticajaShadowCatalogSync.PullUsuariosAsync(ct).ConfigureAwait(false);
                if (!u.Ok) return (u.Ok, u.Error, changes.Count);
            }

            _state.SaveSuccess("catalog", body.ServerTimeUtc, rowHint: changes.Count, cursor: body.NextCursor);
            PosDiagnostics.Log($"multicaja.sync.delta ok changes={changes.Count} cursor={body.NextCursor} productIds={productIds.Count}");
            return (true, null, changes.Count);
        }
        catch (Exception ex)
        {
            _state.SaveFailure("catalog", ex.Message);
            PosDiagnostics.Log("multicaja.sync.delta", ex);
            return (false, ex.Message, 0);
        }
    }

    private sealed class SyncChangesDto
    {
        public long NextCursor { get; set; }
        public DateTime ServerTimeUtc { get; set; }
        public List<SyncChangeDto>? Changes { get; set; }
        public bool ResetRequired { get; set; }
    }

    private sealed class SyncChangeDto
    {
        public string Domain { get; set; } = "";
        public string EntityId { get; set; } = "";
        public string ChangeType { get; set; } = "";
    }
}
