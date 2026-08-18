using Microsoft.AspNetCore.SignalR.Client;
using GrunflexPOS2.Data;

namespace GrunflexPOS2.Services.Multicaja.Sync;

/// <summary>Cliente SignalR para invalidación realtime (sin catálogos completos).</summary>
public sealed class MulticajaHubClient : IAsyncDisposable
{
    private HubConnection? _connection;
    private readonly Func<string> _baseUrl;
    private Action<string, object?>? _onInvalidation;

    public bool IsConnected =>
        _connection?.State == HubConnectionState.Connected;

    public MulticajaHubClient(Func<string>? baseUrl = null)
    {
        _baseUrl = baseUrl ?? (() => AppConfig.Cargar().ApiBaseUrl);
    }

    public void OnEvent(Action<string, object?> handler) => _onInvalidation = handler;

    [Obsolete("Use OnEvent")]
    public void OnInvalidation(Action<string, object?> handler) => OnEvent(handler);

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var baseUrl = (_baseUrl() ?? "").TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return;

        if (_connection != null)
        {
            if (_connection.State == HubConnectionState.Connected)
                return;
            await _connection.DisposeAsync();
        }

        _connection = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/hubs/multicaja-sync")
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) })
            .Build();

        RegisterHandler("product-updated");
        RegisterHandler("inventory-adjusted");
        RegisterHandler("cashier-updated");
        RegisterHandler("caja-updated");
        RegisterHandler("sync-reset");

        _connection.Reconnected += async _ =>
        {
            PosDiagnostics.Log("multicaja.hub reconnected");
            await JoinCajaGroupAsync(ct);
        };

        await _connection.StartAsync(ct);
        await JoinCajaGroupAsync(ct);
        try { App.Connectivity.SignalRConnected = true; } catch { }
        PosDiagnostics.Log("multicaja.hub connected");
    }

    private void RegisterHandler(string eventName)
    {
        if (_connection == null) return;
        _connection.On<object>(eventName, payload =>
        {
            PosDiagnostics.Log($"multicaja.hub event={eventName}");
            _onInvalidation?.Invoke(eventName, payload);
        });
    }

    private async Task JoinCajaGroupAsync(CancellationToken ct)
    {
        if (_connection?.State != HubConnectionState.Connected)
            return;
        var cfg = AppConfig.Cargar();
        if (Guid.TryParse(cfg.CajaId, out var cajaId) && cajaId != Guid.Empty)
            await _connection.InvokeAsync("JoinCaja", cajaId.ToString(), ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
            await _connection.DisposeAsync();
    }
}
