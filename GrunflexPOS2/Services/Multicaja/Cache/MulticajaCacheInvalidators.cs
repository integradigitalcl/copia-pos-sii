namespace GrunflexPOS2.Services.Multicaja.Cache;

public sealed class InventoryCacheInvalidator : CacheInvalidatorBase
{
    public InventoryCacheInvalidator() : base("inventory") { }
}

public sealed class CatalogCacheInvalidator : CacheInvalidatorBase
{
    public CatalogCacheInvalidator() : base("catalog") { }
}

public sealed class CajaCacheInvalidator : CacheInvalidatorBase
{
    public CajaCacheInvalidator() : base("caja") { }
}

public sealed class TerminalCacheInvalidator : CacheInvalidatorBase
{
    public TerminalCacheInvalidator() : base("terminal") { }
}

public sealed class CashierCacheInvalidator : CacheInvalidatorBase
{
    public CashierCacheInvalidator() : base("cashier") { }
}

/// <summary>Registro de invalidadores enterprise (una instancia por app).</summary>
public sealed class MulticajaCacheRegistry
{
    public InventoryCacheInvalidator Inventory { get; } = new();
    public CatalogCacheInvalidator Catalog { get; } = new();
    public CajaCacheInvalidator Caja { get; } = new();
    public TerminalCacheInvalidator Terminal { get; } = new();
    public CashierCacheInvalidator Cashier { get; } = new();

    public void InvalidateAll(string reason)
    {
        Inventory.Invalidate(reason);
        Catalog.Invalidate(reason);
        Caja.Invalidate(reason);
        Terminal.Invalidate(reason);
        Cashier.Invalidate(reason);
    }
}
