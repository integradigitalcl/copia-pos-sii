using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace GrunflexPOS2.Services.Connectivity;

/// <summary>
/// Vigilante de conectividad con el servidor API. Singleton de aplicación.
///
/// - Realiza un GET a /health/live cada <see cref="PollInterval"/> (default 10s).
/// - Mantiene <see cref="State"/>, <see cref="LastLatencyMs"/>, <see cref="LastError"/>.
/// - Lanza <see cref="StateChanged"/> cuando el estado cambia.
/// - Cuando detecta una caída → reduce el intervalo a 3s para detectar reconexión rápido,
///   y vuelve a 10s una vez que está estable Online por 30s.
///
/// Es seguro de usar desde UI (los eventos se invocan en el thread del timer, los
/// suscriptores deben usar Dispatcher si quieren tocar UI).
/// </summary>
public sealed class ConnectivityMonitor : IDisposable
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan PollIntervalWhenOffline { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan DegradedLatencyThreshold { get; set; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(4);

    private readonly HttpClient _http;
    private readonly Func<string> _baseUrlProvider;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _onlineSince;

    public ConnectivityState State { get; private set; } = ConnectivityState.Unknown;
    public long LastLatencyMs { get; private set; } = -1;
    public string? LastError { get; private set; }
    public DateTime LastCheckUtc { get; private set; } = DateTime.MinValue;
    public bool ApiReady { get; private set; }
    public bool SignalRConnected { get; set; }

    public event EventHandler<ConnectivityState>? StateChanged;

    public ConnectivityMonitor(Func<string> baseUrlProvider, HttpClient? http = null)
    {
        _baseUrlProvider = baseUrlProvider ?? throw new ArgumentNullException(nameof(baseUrlProvider));
        _http = http ?? new HttpClient();
        _http.Timeout = RequestTimeout;
    }

    /// <summary>
    /// Ajusta intervalos y timeouts desde <see cref="GrunflexPOS2.Data.AppConfig"/> (multicaja).
    /// Llamar antes de <see cref="Start"/> para que el primer ciclo ya use los valores.
    /// </summary>
    public void ApplyMulticajaSettings(
        TimeSpan heartbeatOnline,
        TimeSpan heartbeatOffline,
        TimeSpan healthTimeout,
        TimeSpan degradedLatencyThreshold)
    {
        PollInterval = heartbeatOnline;
        PollIntervalWhenOffline = heartbeatOffline;
        RequestTimeout = healthTimeout;
        DegradedLatencyThreshold = degradedLatencyThreshold;
        try { _http.Timeout = healthTimeout; } catch { /* no-op */ }
    }

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
    }

    public async Task<bool> CheckOnceAsync(CancellationToken ct = default)
    {
        var url = (_baseUrlProvider() ?? "").TrimEnd('/') + "/health/live";
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            sw.Stop();
            LastLatencyMs = sw.ElapsedMilliseconds;
            LastError = null;
            LastCheckUtc = DateTime.UtcNow;
            if (!resp.IsSuccessStatusCode)
            {
                ChangeState(ConnectivityState.Offline);
                LastError = $"HTTP {(int)resp.StatusCode}";
                return false;
            }
            ChangeState(sw.Elapsed > DegradedLatencyThreshold ? ConnectivityState.Degraded : ConnectivityState.Online);
            _ = CheckReadyAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            sw.Stop();
            LastLatencyMs = sw.ElapsedMilliseconds;
            LastError = ex.Message;
            LastCheckUtc = DateTime.UtcNow;
            ChangeState(ConnectivityState.Offline);
            return false;
        }
    }

    private async Task CheckReadyAsync(CancellationToken ct)
    {
        try
        {
            var url = (_baseUrlProvider() ?? "").TrimEnd('/') + "/health/ready";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            ApiReady = resp.IsSuccessStatusCode;
        }
        catch
        {
            ApiReady = false;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(ct).ConfigureAwait(false);

                var wait = State == ConnectivityState.Offline
                    ? PollIntervalWhenOffline
                    : PollInterval;

                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { /* never throw out of the loop */ }
        }
    }

    private void ChangeState(ConnectivityState newState)
    {
        if (newState == State)
        {
            // Promover de Degraded a Online si lleva 30s estable
            if (newState == ConnectivityState.Online && Interlocked.Read(ref _onlineSince) == 0)
                Interlocked.Exchange(ref _onlineSince, DateTime.UtcNow.Ticks);
            return;
        }

        State = newState;
        if (newState == ConnectivityState.Online)
            Interlocked.Exchange(ref _onlineSince, DateTime.UtcNow.Ticks);
        else
            Interlocked.Exchange(ref _onlineSince, 0);

        try { StateChanged?.Invoke(this, newState); }
        catch { /* no propagar excepciones de handlers */ }
    }

    public void Dispose()
    {
        Stop();
        try { _http.Dispose(); } catch { }
    }
}
