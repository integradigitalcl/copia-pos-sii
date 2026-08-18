using System;
using System.Threading;
using System.Threading.Tasks;
using GrunflexPOS2.Services.API;
using GrunflexPOS2.Services.Connectivity;

namespace GrunflexPOS2.Services.Licensing;

/// <summary>
/// Refresco automático de la licencia contra la API.
///
/// Disparadores:
/// - <b>Startup</b>: a los 10 s del arranque del POS, intenta sincronizar (silent).
/// - <b>Periódico</b>: cada 12 h mientras esté online.
/// - <b>Reconexión</b>: cuando <see cref="ConnectivityMonitor"/> cambia de Offline → Online,
///   re-intenta inmediatamente (no espera al próximo tick).
///
/// La operación efectiva la hace <see cref="LicensingCloudService.TryRefreshAsync"/>, que:
/// - Lee <c>licencia_activation_id</c> local
/// - POST /api/licensing/refresh con activationId + machineName
/// - Si OK: valida con clave pública, escribe <c>licencia_last_cloud_ok_utc=now</c>,
///   refresca <c>ProductEntitlements</c>.
/// - Si la API devuelve "vencida": limpia la licencia local (el cliente se queda sin features
///   comerciales hasta renovar).
///
/// Si no hay <c>activationId</c> local, el servicio queda silencioso (es un POS sin licencia
/// asignada, no hay nada que sincronizar).
/// </summary>
public sealed class LicenseSyncService : IDisposable
{
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan PeriodicInterval { get; set; } = TimeSpan.FromHours(12);

    private readonly ConnectivityMonitor? _connectivity;
    private Timer? _timer;
    private CancellationTokenSource? _cts;
    private int _inFlight;

    public DateTime? LastAttemptUtc { get; private set; }
    public DateTime? LastSuccessUtc { get; private set; }
    public string? LastMessage { get; private set; }
    public event EventHandler? Refreshed;

    public LicenseSyncService(ConnectivityMonitor? connectivity)
    {
        _connectivity = connectivity;
        if (_connectivity != null)
            _connectivity.StateChanged += OnConnectivityChanged;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StartupDelay, _cts.Token).ConfigureAwait(false);
                await TryRefreshAsync(_cts.Token).ConfigureAwait(false);
            }
            catch { }
        });
        _timer = new Timer(_ => SafeRunPeriodic(), null, PeriodicInterval, PeriodicInterval);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _timer?.Dispose(); } catch { }
        _timer = null;
    }

    public async Task<bool> TryRefreshAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _inFlight, 1) == 1) return false; // ya hay uno corriendo
        try
        {
            LastAttemptUtc = DateTime.UtcNow;
            var (ok, msg) = await LicensingCloudService.TryRefreshAsync(silent: true, ct).ConfigureAwait(false);
            LastMessage = msg;
            if (ok)
            {
                LastSuccessUtc = DateTime.UtcNow;
                PosDiagnostics.Log("LicenseSync: sincronización OK con API.");
                try { Telemetry.Telemetry.Track("license.sync.ok"); } catch { }
            }
            else if (!string.IsNullOrEmpty(msg))
            {
                PosDiagnostics.Log("LicenseSync: " + msg);
                try { Telemetry.Telemetry.Track("license.sync.fail", "msg", msg); } catch { }
            }

            try { Refreshed?.Invoke(this, EventArgs.Empty); } catch { }
            return ok;
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("LicenseSync excepción", ex);
            return false;
        }
        finally { Interlocked.Exchange(ref _inFlight, 0); }
    }

    private void SafeRunPeriodic()
    {
        if (_connectivity != null && _connectivity.State == ConnectivityState.Offline) return;
        try { _ = TryRefreshAsync(_cts?.Token ?? CancellationToken.None); }
        catch { }
    }

    private void OnConnectivityChanged(object? sender, ConnectivityState s)
    {
        if (s == ConnectivityState.Online)
            _ = TryRefreshAsync(_cts?.Token ?? CancellationToken.None);
    }

    public void Dispose()
    {
        if (_connectivity != null) _connectivity.StateChanged -= OnConnectivityChanged;
        Stop();
    }
}
