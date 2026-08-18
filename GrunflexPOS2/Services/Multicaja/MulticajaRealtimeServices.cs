using System.Net.Http;
using GrunflexPOS2.Data;
using GrunflexPOS2.Services.Connectivity;
using GrunflexPOS2.Services.Licensing;
using GrunflexPOS2.Services.Multicaja.Cache;
using GrunflexPOS2.Services.Multicaja.Events;
using GrunflexPOS2.Services.Multicaja.Sync;
using GrunflexPOS2.Services.Multicaja.Terminal;

namespace GrunflexPOS2.Services.Multicaja;

/// <summary>Contenedor de servicios realtime multicaja (invalidación + sync + hub).</summary>
public sealed class MulticajaRealtimeServices : IDisposable
{
    private readonly MulticajaBackgroundServices _background;

    public MulticajaCacheRegistry Caches { get; }
    public LocalEventBus EventBus { get; }
    public DeltaSyncService DeltaSync { get; }
    public MulticajaSyncCoordinator Coordinator { get; }
    public MulticajaHubClient? Hub { get; }
    public MulticajaReconnectOrchestrator Reconnect { get; }

    public MulticajaRealtimeServices(
        ConnectivityMonitor connectivity,
        HttpClient http,
        TerminalService? terminal,
        TerminalRegistrationClient? legacyTerminal)
    {
        Caches = new MulticajaCacheRegistry();
        EventBus = new LocalEventBus();
        DeltaSync = new DeltaSyncService(http, () => connectivity.State);

        var capabilities = MulticajaCapabilitiesClient.GetAsync(http).GetAwaiter().GetResult();

        Coordinator = new MulticajaSyncCoordinator(
            DeltaSync,
            Caches,
            EventBus,
            () => connectivity.State,
            capabilities,
            null);

        MulticajaIncrementalSyncService? incremental = null;
        if (capabilities.IncrementalSync)
        {
            incremental = new MulticajaIncrementalSyncService(
                http,
                () => connectivity.State,
                Coordinator);
            Coordinator.AttachIncrementalSync(incremental);
        }

        Hub = capabilities.SignalR ? new MulticajaHubClient() : null;
        if (Hub != null)
            new SignalRMulticajaBridge(Coordinator).Attach(Hub);

        Coordinator.WireInvalidationHandlers();

        var replay = new OfflineReplayService(connectivity);
        _background = new MulticajaBackgroundServices(
            connectivity,
            http,
            terminal,
            legacyTerminal,
            this,
            capabilities,
            incremental);

        Reconnect = _background.Reconnect;
    }

    public MulticajaBackgroundServices Background => _background;

    public void Start(AppConfig cfg) => _background.Start(cfg);

    public void Dispose() => _background.Dispose();
}
