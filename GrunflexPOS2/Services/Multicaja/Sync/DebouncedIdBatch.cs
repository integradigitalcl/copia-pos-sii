using System.Collections.Concurrent;

namespace GrunflexPOS2.Services.Multicaja.Sync;

/// <summary>Agrupa IDs y ejecuta flush tras debounce (evita N sync por N eventos SignalR).</summary>
public sealed class DebouncedIdBatch
{
    private readonly ConcurrentDictionary<int, byte> _productIds = new();
    private readonly ConcurrentDictionary<Guid, byte> _userIds = new();
    private readonly TimeSpan _delay;
    private readonly Func<IReadOnlyList<int>, IReadOnlyList<Guid>, CancellationToken, Task> _flushAsync;
    private CancellationTokenSource? _debounceCts = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly string _name;

    public DebouncedIdBatch(
        string name,
        TimeSpan delay,
        Func<IReadOnlyList<int>, IReadOnlyList<Guid>, CancellationToken, Task> flushAsync)
    {
        _name = name;
        _delay = delay;
        _flushAsync = flushAsync;
    }

    public void EnqueueProduct(int productId)
    {
        if (productId <= 0) return;
        _productIds.TryAdd(productId, 0);
        ScheduleFlush();
    }

    public void EnqueueProducts(IEnumerable<int> ids)
    {
        var any = false;
        foreach (var id in ids)
        {
            if (id <= 0) continue;
            _productIds.TryAdd(id, 0);
            any = true;
        }
        if (any) ScheduleFlush();
    }

    public void EnqueueUser(Guid userId)
    {
        if (userId == Guid.Empty) return;
        _userIds.TryAdd(userId, 0);
        ScheduleFlush();
    }

    public async Task FlushNowAsync(CancellationToken ct = default)
    {
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var products = DrainProducts();
            var users = DrainUsers();
            if (products.Count == 0 && users.Count == 0)
                return;
            PosDiagnostics.Log(
                $"multicaja.debounce flush name={_name} products={products.Count} users={users.Count}");
            await _flushAsync(products, users, ct).ConfigureAwait(false);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private void ScheduleFlush()
    {
        var next = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _debounceCts, next);
        try { prev?.Cancel(); } catch { }
        prev?.Dispose();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_delay, next.Token).ConfigureAwait(false);
                await FlushNowAsync(next.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                PosDiagnostics.Log($"multicaja.debounce canceled name={_name}");
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log($"multicaja.debounce error name={_name}", ex);
            }
        });
    }

    private List<int> DrainProducts()
    {
        var list = _productIds.Keys.ToList();
        _productIds.Clear();
        return list;
    }

    private List<Guid> DrainUsers()
    {
        var list = _userIds.Keys.ToList();
        _userIds.Clear();
        return list;
    }
}
