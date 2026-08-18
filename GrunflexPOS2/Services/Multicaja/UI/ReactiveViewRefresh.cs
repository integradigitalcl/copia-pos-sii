using System.Windows;
using System.Windows.Threading;
using GrunflexPOS2.Services.Multicaja.Cache;
using GrunflexPOS2.Services.Multicaja.Events;

namespace GrunflexPOS2.Services.Multicaja.UI;

/// <summary>
/// Suscripción reactiva: invalidación de cache → cancela carga anterior → refresh en UI thread.
/// </summary>
public sealed class ReactiveViewRefresh : IDisposable
{
    private readonly FrameworkElement _view;
    private readonly Func<CancellationToken, Task> _refreshAsync;
    private readonly CacheInvalidatorBase[] _invalidators;
    private int _refreshGeneration;

    public ReactiveViewRefresh(
        FrameworkElement view,
        Func<CancellationToken, Task> refreshAsync,
        CacheInvalidatorBase primary,
        params CacheInvalidatorBase[] additional)
    {
        _view = view;
        _refreshAsync = refreshAsync;
        _invalidators = new[] { primary }.Concat(additional).ToArray();

        foreach (var inv in _invalidators)
            inv.Invalidated += OnInvalidated;
    }

    public ReactiveViewRefresh SubscribeBus(LocalEventBus bus, Action<LocalEventBus> subscribe)
    {
        subscribe(bus);
        return this;
    }

    public static ReactiveViewRefresh ForInventory(
        FrameworkElement view,
        Func<CancellationToken, Task> refreshAsync,
        MulticajaCacheRegistry caches,
        LocalEventBus? bus = null)
    {
        var r = new ReactiveViewRefresh(
            view,
            refreshAsync,
            caches.Inventory,
            caches.Catalog);

        if (bus != null)
        {
            bus.InventoryChanged += (_, _) => caches.Inventory.Invalidate("bus:inventory");
            bus.CatalogChanged += (_, _) => caches.Catalog.Invalidate("bus:catalog");
            bus.SyncCompleted += (_, e) =>
            {
                if (e.Domain is "products-by-ids" or "catalog" or "incremental")
                    caches.Inventory.Invalidate("bus:sync-completed");
            };
        }

        return r;
    }

    private void OnInvalidated(string reason)
    {
        if (!_view.IsLoaded)
            return;

        var gen = Interlocked.Increment(ref _refreshGeneration);
        var token = _invalidators[0].CurrentToken;

        _ = _view.Dispatcher.InvokeAsync(async () =>
        {
            if (!_view.IsLoaded || gen != Volatile.Read(ref _refreshGeneration))
                return;

            try
            {
                PosDiagnostics.Log($"multicaja.ui refresh start view={_view.GetType().Name} reason={reason}");
                await _refreshAsync(token).ConfigureAwait(true);
                PosDiagnostics.Log($"multicaja.ui refresh completed view={_view.GetType().Name}");
            }
            catch (OperationCanceledException)
            {
                PosDiagnostics.Log($"multicaja.ui refresh canceled view={_view.GetType().Name} reason={reason}");
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log($"multicaja.ui refresh error view={_view.GetType().Name}", ex);
            }
        }, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        foreach (var inv in _invalidators)
            inv.Invalidated -= OnInvalidated;
    }
}
