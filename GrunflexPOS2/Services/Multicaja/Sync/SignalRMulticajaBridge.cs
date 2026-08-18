using System.Text.Json;
using GrunflexPOS2.Services.Multicaja.Cache;

namespace GrunflexPOS2.Services.Multicaja.Sync;

/// <summary>Traduce eventos SignalR → invalidación + coordinador (sin refresh UI directo).</summary>
public sealed class SignalRMulticajaBridge
{
    private readonly MulticajaSyncCoordinator _coordinator;

    public SignalRMulticajaBridge(MulticajaSyncCoordinator coordinator) => _coordinator = coordinator;

    public void Attach(MulticajaHubClient hub)
    {
        hub.OnEvent(HandleHubEvent);
    }

    private void HandleHubEvent(string eventName, object? payload)
    {
        var (productId, userId) = TryParsePayload(payload);
        _coordinator.HandleRealtimeEvent(eventName, productId, userId);
    }

    private static (int? productId, Guid? userId) TryParsePayload(object? payload)
    {
        if (payload == null) return (null, null);

        try
        {
            var json = payload is JsonElement el
                ? el
                : JsonSerializer.SerializeToElement(payload);

            int? pid = null;
            if (json.TryGetProperty("productId", out var p1) && p1.TryGetInt32(out var v1))
                pid = v1;
            else if (json.TryGetProperty("entityId", out var e) && int.TryParse(e.GetString(), out var v2))
                pid = v2;

            Guid? uid = null;
            if (json.TryGetProperty("userId", out var u) && Guid.TryParse(u.GetString(), out var g))
                uid = g;

            return (pid, uid);
        }
        catch
        {
            return (null, null);
        }
    }
}
