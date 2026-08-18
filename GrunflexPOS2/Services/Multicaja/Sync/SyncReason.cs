namespace GrunflexPOS2.Services.Multicaja.Sync;

public enum SyncReason
{
    Bootstrap,
    Periodic,
    Reconnected,
    Manual,
    Login,
    InventoryOpened
}
