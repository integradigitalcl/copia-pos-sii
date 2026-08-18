using System.Net.Http;
using GrunflexPOS2.Data;
using GrunflexPOS2.Services.Connectivity;
using GrunflexPOS2.Services.Licensing;
using GrunflexPOS2.Services.Multicaja.Terminal;

namespace GrunflexPOS2.Services.Multicaja.Sync;

/// <summary>Sync periódico, heartbeat, hub SignalR y reconexión (usa MulticajaRealtimeServices).</summary>
public sealed class MulticajaBackgroundServices : IDisposable
{
    private readonly ConnectivityMonitor _connectivity;
    private readonly MulticajaReconnectOrchestrator _reconnect;
    private readonly MulticajaRealtimeServices _realtime;
    private readonly TerminalService? _terminal;
    private readonly MulticajaIncrementalSyncService? _incremental;
    private readonly MulticajaCapabilities _capabilities;
    private Timer? _periodicTimer;
    private bool _wasOffline = true;

    public MulticajaBackgroundServices(
        ConnectivityMonitor connectivity,
        HttpClient http,
        TerminalService? terminal,
        TerminalRegistrationClient? legacyTerminal,
        MulticajaRealtimeServices realtime,
        MulticajaCapabilities capabilities,
        MulticajaIncrementalSyncService? incremental)
    {
        _connectivity = connectivity;
        _realtime = realtime;
        _terminal = terminal;
        _capabilities = capabilities;
        _incremental = incremental;

        var replay = new OfflineReplayService(connectivity);
        _reconnect = new MulticajaReconnectOrchestrator(
            realtime.Coordinator,
            realtime.Caches,
            terminal,
            legacyTerminal,
            realtime.Hub,
            _incremental,
            replay,
            capabilities);
    }

    public MulticajaSyncCoordinator Sync => _realtime.Coordinator;
    public MulticajaReconnectOrchestrator Reconnect => _reconnect;

    public void Start(AppConfig cfg)
    {
        if (!MulticajaRuntime.UseApiOnlyClient)
            return;

        var intervalSec = cfg.MulticajaCatalogSyncIntervalSeconds;
        if (intervalSec < 15) intervalSec = 15;
        if (intervalSec > 600) intervalSec = 600;

        _connectivity.StateChanged += OnConnectivityChanged;
        _periodicTimer = new Timer(_ => _ = RunPeriodicAsync(), null,
            TimeSpan.FromSeconds(intervalSec),
            TimeSpan.FromSeconds(intervalSec));

        if (_terminal != null)
            _terminal.StartHeartbeat(TimeSpan.FromSeconds(75));

        if (_realtime.Hub != null)
            _ = _realtime.Hub.ConnectAsync();

        PosDiagnostics.Log(
            $"multicaja.background started syncInterval={intervalSec}s incremental={_capabilities.IncrementalSync} signalR={_capabilities.SignalR}");
    }

    private void OnConnectivityChanged(object? sender, ConnectivityState state)
    {
        if (state == ConnectivityState.Offline)
        {
            _wasOffline = true;
            _reconnect.OnBecameOffline();
            return;
        }

        if (state is ConnectivityState.Online or ConnectivityState.Degraded && _wasOffline)
        {
            _wasOffline = false;
            _ = _reconnect.OnBecameOnlineAsync();
        }
    }

    private async Task RunPeriodicAsync()
    {
        try
        {
            if (_connectivity.State == ConnectivityState.Offline)
                return;

            if (_capabilities.IncrementalSync && _incremental != null)
                await _incremental.PullChangesAsync().ConfigureAwait(false);
            else
                await Sync.SyncNowAsync(SyncReason.Periodic).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("multicaja.background periodic", ex);
        }
    }

    public void Dispose()
    {
        try { _connectivity.StateChanged -= OnConnectivityChanged; } catch { }
        try { _periodicTimer?.Dispose(); } catch { }
        _terminal?.StopHeartbeat();
        if (_realtime.Hub != null)
            _ = _realtime.Hub.DisposeAsync();
    }
}
