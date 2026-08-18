using GrunflexPOS2.Services.Connectivity;
using GrunflexPOS2.Services.Offline;

namespace GrunflexPOS2.Services.Multicaja;

/// <summary>Replay idempotente de la cola offline (coordinado con reconnect).</summary>
public sealed class OfflineReplayService
{
    private readonly ConnectivityMonitor? _connectivity;

    public OfflineReplayService(ConnectivityMonitor? connectivity) => _connectivity = connectivity;

    public async Task<(int Processed, int Pending)> ReplayAsync(CancellationToken ct = default)
    {
        if (App.OfflineQueue == null)
            return (0, 0);

        if (_connectivity != null && _connectivity.State == ConnectivityState.Offline)
            return (0, App.OfflineQueue.PendingCount());

        var before = App.OfflineQueue.PendingCount();
        PosDiagnostics.Log($"multicaja.replay start pending={before}");
        await App.OfflineQueue.ReplayAsync(ct).ConfigureAwait(false);
        var after = App.OfflineQueue.PendingCount();
        PosDiagnostics.Log($"multicaja.replay done pending={after}");
        return (before - after, after);
    }
}
