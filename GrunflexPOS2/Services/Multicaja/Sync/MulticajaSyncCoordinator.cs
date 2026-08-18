using GrunflexPOS2.Services.Connectivity;
using GrunflexPOS2.Services.Multicaja.Cache;
using GrunflexPOS2.Services.Multicaja.Events;

namespace GrunflexPOS2.Services.Multicaja.Sync;

/// <summary>
/// Orquesta invalidaciones, debounce, delta sync por IDs y eventos locales (UI desacoplada de SignalR).
/// </summary>
public sealed class MulticajaSyncCoordinator
{
    private readonly SyncStateStore _state = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<ConnectivityState> _connectivityState;
    private readonly MulticajaCapabilities? _capabilities;
    private readonly DeltaSyncService _delta;
    private readonly MulticajaCacheRegistry _caches;
    private readonly LocalEventBus _bus;
    private readonly DebouncedIdBatch _productBatch;
    private MulticajaIncrementalSyncService? _incremental;
    private DateTime _lastRunUtc = DateTime.MinValue;

    public TimeSpan MinIntervalBetweenRuns { get; set; } = TimeSpan.FromSeconds(4);
    public TimeSpan DebounceInterval { get; set; } = TimeSpan.FromMilliseconds(1500);

    public MulticajaSyncCoordinator(
        DeltaSyncService delta,
        MulticajaCacheRegistry caches,
        LocalEventBus bus,
        Func<ConnectivityState>? connectivityState = null,
        MulticajaCapabilities? capabilities = null,
        MulticajaIncrementalSyncService? incremental = null)
    {
        _delta = delta;
        _caches = caches;
        _bus = bus;
        _connectivityState = connectivityState ?? (() => ConnectivityState.Unknown);
        _capabilities = capabilities;
        _incremental = incremental;
        _productBatch = new DebouncedIdBatch(
            "products",
            DebounceInterval,
            FlushDebouncedAsync);
    }

    public void AttachIncrementalSync(MulticajaIncrementalSyncService incremental) =>
        _incremental = incremental;

    public void WireInvalidationHandlers()
    {
        _caches.Inventory.Invalidated += reason => OnDomainInvalidated("inventory", reason);
        _caches.Catalog.Invalidated += reason => OnDomainInvalidated("catalog", reason);
        _caches.Cashier.Invalidated += reason => OnDomainInvalidated("cashier", reason);
        _caches.Caja.Invalidated += reason => OnDomainInvalidated("caja", reason);
        _caches.Terminal.Invalidated += reason => OnDomainInvalidated("terminal", reason);
    }

    /// <summary>SignalR u otro origen externo: encola IDs y debounce (no toca UI).</summary>
    public void HandleRealtimeEvent(string eventName, int? productId = null, Guid? userId = null)
    {
        PosDiagnostics.Log($"multicaja.signalr event={eventName} productId={productId} userId={userId}");

        switch (eventName)
        {
            case "inventory-adjusted":
            case "product-updated":
                if (productId is int pid && pid > 0)
                {
                    _caches.Inventory.Invalidate(eventName);
                    _caches.Catalog.Invalidate(eventName);
                    _productBatch.EnqueueProduct(pid);
                }
                else
                {
                    _caches.Inventory.Invalidate(eventName);
                    _ = RunIncrementalOrFullAsync(SyncReason.Manual);
                }
                break;
            case "cashier-updated":
                _caches.Cashier.Invalidate(eventName);
                if (userId is { } uid && uid != Guid.Empty)
                    _productBatch.EnqueueUser(uid);
                else
                    _ = RunIncrementalOrFullAsync(SyncReason.Manual);
                break;
            case "caja-updated":
                _caches.Caja.Invalidate(eventName);
                _bus.Publish(new CajaChangedEvent { Source = eventName });
                break;
            case "sync-reset":
                _caches.InvalidateAll("sync-reset");
                _ = SyncNowAsync(SyncReason.Manual);
                break;
            default:
                _caches.Catalog.Invalidate(eventName);
                break;
        }
    }

    public void EnqueueProductIds(IEnumerable<int> productIds, string source)
    {
        _caches.Inventory.Invalidate(source);
        _caches.Catalog.Invalidate(source);
        _productBatch.EnqueueProducts(productIds);
    }

    public Task FlushPendingBatchesAsync(CancellationToken ct = default) =>
        _productBatch.FlushNowAsync(ct);

    public Task SyncProductsByIdsAsync(IReadOnlyList<int> productIds, CancellationToken ct = default) =>
        _delta.PullProductsByIdsAsync(productIds, ct);

    public async Task<(bool Ok, string? Error)> SyncNowAsync(SyncReason reason, CancellationToken ct = default)
    {
        if (!MulticajaRuntime.UseApiOnlyClient)
            return (false, "No aplica");

        if (_connectivityState() == ConnectivityState.Offline)
            return (false, "Sin conexión con el servidor.");

        if (reason != SyncReason.Bootstrap && reason != SyncReason.Manual && reason != SyncReason.InventoryOpened)
        {
            var since = DateTime.UtcNow - _lastRunUtc;
            if (_lastRunUtc != DateTime.MinValue && since < MinIntervalBetweenRuns)
                return (true, null);
        }

        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return (true, null);

        SyncStatusService.SetRunning(true);
        try
        {
            PosDiagnostics.Log($"multicaja.sync start reason={reason}");
            (bool Ok, string? Error) result;

            if (reason == SyncReason.Periodic && _capabilities?.IncrementalSync == true && _incremental != null)
            {
                var inc = await _incremental.PullChangesAsync(ct).ConfigureAwait(false);
                result = (inc.Ok, inc.Error);
            }
            else if (reason == SyncReason.Bootstrap || reason == SyncReason.Reconnected)
            {
                result = await MulticajaShadowCatalogSync.PullTodoAsync(ct).ConfigureAwait(false);
            }
            else
            {
                result = await MulticajaShadowCatalogSync.PullProductosAsync(ct).ConfigureAwait(false);
            }

            _lastRunUtc = DateTime.UtcNow;

            if (result.Ok)
            {
                var now = DateTime.UtcNow;
                _state.SaveSuccess("catalog", now);
                App.LastMulticajaCatalogSyncUtc = now;
                SyncStatusService.SetSuccess(reason, now);
                _caches.Inventory.Invalidate($"sync-ok:{reason}");
                _caches.Catalog.Invalidate($"sync-ok:{reason}");
                _bus.Publish(new SyncCompletedEvent
                {
                    Domain = "catalog",
                    ItemCount = 0,
                    Reason = reason
                });
                PosDiagnostics.Log($"multicaja.sync ok reason={reason}");
            }
            else
            {
                _state.SaveFailure("catalog", result.Error ?? "Error desconocido");
                SyncStatusService.SetFailure(reason, result.Error ?? "Error desconocido");
                _bus.Publish(new SyncFailedEvent
                {
                    Domain = "catalog",
                    Error = result.Error ?? "Error desconocido"
                });
            }

            return result;
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            _state.SaveFailure("catalog", msg);
            SyncStatusService.SetFailure(reason, msg);
            _bus.Publish(new SyncFailedEvent { Domain = "catalog", Error = msg });
            PosDiagnostics.Log("multicaja.sync exception", ex);
            return (false, msg);
        }
        finally
        {
            SyncStatusService.SetRunning(false);
            _gate.Release();
        }
    }

    public Task<(bool Ok, string? Error)> PullBootstrapFullAsync(CancellationToken ct = default) =>
        SyncNowAsync(SyncReason.Bootstrap, ct);

    private void OnDomainInvalidated(string domain, string reason)
    {
        PosDiagnostics.Log($"multicaja.cache route domain={domain} reason={reason}");
    }

    private async Task FlushDebouncedAsync(
        IReadOnlyList<int> productIds,
        IReadOnlyList<Guid> userIds,
        CancellationToken ct)
    {
        if (_connectivityState() == ConnectivityState.Offline)
            return;

        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false))
            return;

        try
        {
            if (productIds.Count > 0)
            {
                var r = await _delta.PullProductsByIdsAsync(productIds, ct).ConfigureAwait(false);
                if (r.Ok)
                {
                    _bus.Publish(new InventoryChangedEvent
                    {
                        ProductIds = productIds,
                        Source = "debounce-batch"
                    });
                    _bus.Publish(new CatalogChangedEvent
                    {
                        ProductIds = productIds,
                        Source = "debounce-batch"
                    });
                    _bus.Publish(new SyncCompletedEvent
                    {
                        Domain = "products-by-ids",
                        ItemCount = r.Count,
                        Reason = SyncReason.Manual
                    });
                    PosDiagnostics.Log($"multicaja.ui refresh signal products={productIds.Count}");
                }
                else
                {
                    _bus.Publish(new SyncFailedEvent { Domain = "products-by-ids", Error = r.Error ?? "?" });
                }
            }

            if (userIds.Count > 0)
            {
                var u = await _delta.PullUsersByIdsAsync(userIds, ct).ConfigureAwait(false);
                if (u.Ok)
                {
                    _caches.Cashier.Invalidate("users-synced");
                    _bus.Publish(new CashierChangedEvent { UserIds = userIds, Source = "debounce-batch" });
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task RunIncrementalOrFullAsync(SyncReason reason)
    {
        if (_capabilities?.IncrementalSync == true && _incremental != null)
            return RunIncrementalAsync(reason);
        return SyncNowAsync(reason);
    }

    private async Task RunIncrementalAsync(SyncReason reason)
    {
        var inc = await _incremental!.PullChangesAsync().ConfigureAwait(false);
        if (inc.Ok)
            _bus.Publish(new SyncCompletedEvent
            {
                Domain = "incremental",
                ItemCount = inc.ChangeCount,
                Reason = reason
            });
    }
}
