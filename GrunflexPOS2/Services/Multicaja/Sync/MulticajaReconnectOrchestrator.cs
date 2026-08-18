using GrunflexPOS2.Services.Connectivity;
using GrunflexPOS2.Services.Licensing;
using GrunflexPOS2.Services.Multicaja.Cache;
using GrunflexPOS2.Services.Multicaja.Terminal;

namespace GrunflexPOS2.Services.Multicaja.Sync;

/// <summary>Reconnect coordinado: hub → heartbeat → replay → sync → invalidación caches.</summary>
public sealed class MulticajaReconnectOrchestrator
{
    private readonly MulticajaSyncCoordinator _sync;
    private readonly MulticajaCacheRegistry _caches;
    private readonly TerminalService? _terminal;
    private readonly TerminalRegistrationClient? _legacyTerminal;
    private readonly MulticajaHubClient? _hub;
    private readonly MulticajaIncrementalSyncService? _incremental;
    private readonly OfflineReplayService? _replay;
    private readonly MulticajaCapabilities? _capabilities;
    private int _reconnectInFlight;

    public MulticajaReconnectPhase Phase { get; private set; } = MulticajaReconnectPhase.Offline;
    public event EventHandler<MulticajaReconnectPhase>? PhaseChanged;

    public MulticajaReconnectOrchestrator(
        MulticajaSyncCoordinator sync,
        MulticajaCacheRegistry caches,
        TerminalService? terminal,
        TerminalRegistrationClient? legacyTerminal,
        MulticajaHubClient? hub,
        MulticajaIncrementalSyncService? incremental,
        OfflineReplayService? replay,
        MulticajaCapabilities? capabilities)
    {
        _sync = sync;
        _caches = caches;
        _terminal = terminal;
        _legacyTerminal = legacyTerminal;
        _hub = hub;
        _incremental = incremental;
        _replay = replay;
        _capabilities = capabilities;
    }

    public async Task OnBecameOnlineAsync(CancellationToken ct = default)
    {
        if (!MulticajaRuntime.UseApiOnlyClient)
            return;

        if (Interlocked.CompareExchange(ref _reconnectInFlight, 1, 0) != 0)
            return;

        try
        {
            SetPhase(MulticajaReconnectPhase.Reconnecting);
            PosDiagnostics.Log("multicaja.reconnect: start");

            if (_capabilities?.SignalR == true && _hub != null)
            {
                try { await _hub.ConnectAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) { PosDiagnostics.Log("multicaja.reconnect hub", ex); }
            }

            if (_terminal != null)
            {
                await _terminal.EnsureRegisteredAsync(ct).ConfigureAwait(false);
                await _terminal.SendHeartbeatNowAsync(reconnect: true, ct).ConfigureAwait(false);
            }
            else if (_legacyTerminal != null)
            {
                await _legacyTerminal.EnsureRegisteredAsync(ct).ConfigureAwait(false);
                await _legacyTerminal.SendHeartbeatNowAsync(ct).ConfigureAwait(false);
            }

            if (_replay != null)
                await _replay.ReplayAsync(ct).ConfigureAwait(false);
            else if (App.OfflineQueue != null)
                await App.OfflineQueue.ReplayAsync(ct).ConfigureAwait(false);

            SetPhase(MulticajaReconnectPhase.Syncing);
            if (_capabilities?.IncrementalSync == true && _incremental != null)
                await _incremental.PullChangesAsync(ct).ConfigureAwait(false);
            else
                await _sync.SyncNowAsync(SyncReason.Reconnected, ct).ConfigureAwait(false);

            _caches.InvalidateAll("reconnect");

            SetPhase(MulticajaReconnectPhase.Online);
            PosDiagnostics.Log("multicaja.reconnect: complete");
        }
        catch (Exception ex)
        {
            SetPhase(MulticajaReconnectPhase.Degraded);
            PosDiagnostics.Log("multicaja.reconnect failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectInFlight, 0);
        }
    }

    public void OnBecameOffline() => SetPhase(MulticajaReconnectPhase.Offline);

    private void SetPhase(MulticajaReconnectPhase phase)
    {
        if (Phase == phase) return;
        Phase = phase;
        try { PhaseChanged?.Invoke(this, phase); } catch { }
    }
}
