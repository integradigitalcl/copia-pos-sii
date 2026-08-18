namespace GrunflexPOS2.Services.Multicaja.Cache;

/// <summary>
/// Invalidación por generación: cancela operaciones en curso y emite evento para refresh reactivo.
/// </summary>
public abstract class CacheInvalidatorBase
{
    private CancellationTokenSource _current = new();
    private long _generation;

    public string Name { get; }
    public long Generation => Volatile.Read(ref _generation);
    public CancellationToken CurrentToken => _current.Token;

    public event Action<string>? Invalidated;

    protected CacheInvalidatorBase(string name) => Name = name;

    public void Invalidate(string reason = "")
    {
        var previous = Interlocked.Exchange(ref _current, new CancellationTokenSource());
        Interlocked.Increment(ref _generation);
        try
        {
            previous.Cancel();
        }
        catch { /* */ }
        finally
        {
            previous.Dispose();
        }

        var tag = string.IsNullOrWhiteSpace(reason) ? Name : $"{Name}:{reason}";
        PosDiagnostics.Log($"multicaja.cache invalidated gen={Generation} {tag}");
        try { Invalidated?.Invoke(tag); } catch { /* handlers no propagan */ }
    }
}
