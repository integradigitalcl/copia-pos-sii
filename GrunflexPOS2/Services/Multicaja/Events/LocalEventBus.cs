namespace GrunflexPOS2.Services.Multicaja.Events;

using GrunflexPOS2.Services.Multicaja.Sync;

public sealed class InventoryChangedEvent
{
    public IReadOnlyList<int> ProductIds { get; init; } = Array.Empty<int>();
    public string Source { get; init; } = "";
}

public sealed class CatalogChangedEvent
{
    public IReadOnlyList<int> ProductIds { get; init; } = Array.Empty<int>();
    public string Source { get; init; } = "";
}

public sealed class CashierChangedEvent
{
    public IReadOnlyList<Guid> UserIds { get; init; } = Array.Empty<Guid>();
    public string Source { get; init; } = "";
}

public sealed class CajaChangedEvent
{
    public Guid? CajaId { get; init; }
    public string Source { get; init; } = "";
}

public sealed class TerminalUpdatedEvent
{
    public string Source { get; init; } = "";
}

public sealed class SyncCompletedEvent
{
    public string Domain { get; init; } = "";
    public int ItemCount { get; init; }
    public SyncReason Reason { get; init; }
}

public sealed class SyncFailedEvent
{
    public string Domain { get; init; } = "";
    public string Error { get; init; } = "";
}

/// <summary>Bus local desacoplado: la UI reacciona aquí, no a SignalR directamente.</summary>
public sealed class LocalEventBus
{
    public event EventHandler<InventoryChangedEvent>? InventoryChanged;
    public event EventHandler<CatalogChangedEvent>? CatalogChanged;
    public event EventHandler<CashierChangedEvent>? CashierChanged;
    public event EventHandler<CajaChangedEvent>? CajaChanged;
    public event EventHandler<TerminalUpdatedEvent>? TerminalUpdated;
    public event EventHandler<SyncCompletedEvent>? SyncCompleted;
    public event EventHandler<SyncFailedEvent>? SyncFailed;

    public void Publish(InventoryChangedEvent e) =>
        SafeInvoke(InventoryChanged, e);

    public void Publish(CatalogChangedEvent e) =>
        SafeInvoke(CatalogChanged, e);

    public void Publish(CashierChangedEvent e) =>
        SafeInvoke(CashierChanged, e);

    public void Publish(CajaChangedEvent e) =>
        SafeInvoke(CajaChanged, e);

    public void Publish(TerminalUpdatedEvent e) =>
        SafeInvoke(TerminalUpdated, e);

    public void Publish(SyncCompletedEvent e) =>
        SafeInvoke(SyncCompleted, e);

    public void Publish(SyncFailedEvent e) =>
        SafeInvoke(SyncFailed, e);

    private static void SafeInvoke<T>(EventHandler<T>? handlers, T args)
    {
        if (handlers == null) return;
        foreach (var h in handlers.GetInvocationList())
        {
            try { ((EventHandler<T>)h).Invoke(null, args); }
            catch { /* */ }
        }
    }
}
